# Spec: Event-and-output framework

> **Status:** authored 2026-05-04; supersedes `spec/tech-plan.md`
> §8 + §9 per `docs/PROJECT-PLAN-2026-05.md` Part 3 + Part 4.
> Stage 10 (review mode) is reframed as the first non-built-in
> framework consumer; original §10 content stays as the feature
> plan that builds on top of this substrate.

This spec is the technical specification for the post-Stage-7
extensibility substrate: the framework that makes pty-speak's
backend usable for as many different input and output mechanisms
as possible. It commits to specific design positions on each of
the open questions in
[`docs/research/MAY-4.md`](../docs/research/MAY-4.md) (the
maintainer-authored prior-art seed); the spec disambiguates by
deciding.

The spec covers **two of MAY-4.md's three concerns**:

- **Concern 1** — universal event routing (the input + dispatcher
  side; how every input source — keyboard today, HID / OSC / MIDI
  / serial in the future — flows through one named dispatch path
  with built-in and user-defined handlers registered through the
  same API).
- **Concern 2** — output framework (the emission + channel +
  profile side; the typed semantic event stream that today only
  reaches NVDA but is built so other output mechanisms — JAWS /
  Narrator / Piper TTS / WASAPI earcons / spatial-audio engines
  / multi-line refreshable braille / FileLogger / network fan-out
  / plain stdout — can subscribe and choose what to do with each
  event).

**Concern 3** (navigable streaming response queue) is explicitly
out of scope. It gets its own spec when Stage 10 starts and the
substrate this spec defines is in place.

## What this spec is, what it isn't

**This spec is:**

- A commitment to a specific architecture — types, abstractions,
  module boundaries, threading model, performance budget — for
  the framework substrate.
- A migration plan that lands the framework across multiple
  walking-skeleton sub-stages (8a → 8f for the output side, 9a →
  9d for the input side), each independently NVDA-validatable.
- A reframing of `spec/tech-plan.md` §8 (interactive list
  detection), §9 (earcons), and §10 (review mode + structured
  navigation): §8 and §9 are absorbed as profiles + channels of
  the new framework; §10 is preserved as a feature stage that
  sits on top of the framework rather than being implemented
  directly against the screen buffer.
- A document that future Claude Code sessions or human
  contributors can read at the top of any sub-stage's PR and
  understand what they ship, what abstractions they reuse, and
  how to NVDA-validate the result.

**This spec is not:**

- An implementation. Each sub-stage (8a, 8b, ...) ships as its
  own PR with its own NVDA validation cycle.
- An expansion of MAY-4.md. The research seed gathered prior art
  and surfaced questions; the spec answers the questions and
  cites the prior art where it shaped a decision.
- A complete specification of every channel, profile, or input
  source the framework will eventually support. v1 ships a
  modest channel surface (NVDA + FileLogger + WASAPI earcons)
  and two profiles (Stream + Selection); the rest are deferred
  with explicit rationale and the abstractions designed so they
  slot in cleanly.

## Anchors and cross-references

- [`docs/research/MAY-4.md`](../docs/research/MAY-4.md) — the
  prior-art seed. Each Decisions Committed entry below references
  the question(s) from MAY-4.md it answers.
- [`docs/STAGE-7-ISSUES.md`](../docs/STAGE-7-ISSUES.md) — the
  empirical NVDA-validation findings. The framework cycle's
  research-phase taxonomy maps each `[output-*]` and `[input-*]`
  entry into a sub-stage of this spec; verbose readback
  (`[output-stream]`) is the headline manifestation Concern 2
  addresses.
- [`spec/overview.md`](overview.md) — the architectural
  rationale; this spec extends, not replaces, that document.
- [`spec/tech-plan.md`](tech-plan.md) — the canonical
  stage-by-stage plan; this spec adds the sub-stage breakdown
  for Part 3 (Output framework cycle) and Part 4 (Input framework
  cycle) that the May-2026 plan announced.
- [`docs/PROJECT-PLAN-2026-05.md`](../docs/PROJECT-PLAN-2026-05.md)
  — the strategic plan; Part 3 + Part 4 cite this spec as the
  research-phase deliverable.

## Design principles (the cross-cutting rubric)

These principles apply to every decision in the spec and to every
sub-stage that lands. They are drawn from MAY-4.md's
"Cross-cutting considerations" section and from the project's
established conventions.

**Failure modes are accessibility issues.** A misbehaving handler,
a profile load failure, a sink error (NVDA not running, Piper
subprocess died, log file not writable) cannot crash pty-speak
and cannot be silent. The user needs to know what failed and how
to recover. The `Ctrl+Shift+H` health-check pattern (one
keystroke, one-line answer, alive/dead probe) is the right
precedent for failure surfacing; the framework reuses it.

**Discoverability through the same channel as use.** Whatever
extension and customisation surfaces emerge, they must be
enumerable at runtime via the same NVDA-readable channel that
everything else uses. "List all intents," "list current handlers
on intent X," "describe profile entry for Y" — if these are
hotkey-accessible they are discoverable; if they require reading
source code they are not.

**Documentation as deliverable.** The
[`docs/UPDATE-FAILURES.md`](../docs/UPDATE-FAILURES.md) precedent
(enumerating literal NVDA announcements) is an unusually
accessible way to document a system's behaviour. Each sub-stage
of this framework ships with the documentation that lets users
debug an unexpected announcement by grepping their way to its
source (e.g. `EVENTS.md`, `OUTPUT-PROFILES.md`).

**Performance budget on the keystroke path.** The user receives
per-character audio feedback at typing speed. Anything more than
~1 ms of overhead per keystroke at the 90th percentile is felt.
The framework's dispatcher and intent layer measure and budget
this explicitly; allocations on the hot path are forbidden;
runtime-context computation is lazy.

**Backward compatibility through the transition.** Existing
pty-speak behaviour remains the default. Each framework sub-stage
expresses prior behaviour as the default configuration of the new
framework, with no behaviour change at the user-visible level
until the framework is in place; subsequent stages introduce
deliberate change with each step verified against
[`docs/ACCESSIBILITY-TESTING.md`](../docs/ACCESSIBILITY-TESTING.md).

**Forward compatibility of the OutputEvent schema.** New output
devices appear regularly — spatial-audio engines and multi-line
braille displays are current examples; the next generation will
include things this document cannot name. The OutputEvent schema
and its metadata are the contract that determines whether
absorbing those devices is a one-channel-implementation cost or
a costly retrofit through the parser, screen model, and emission
sites. The schema is generous with metadata at emission time
(carries semantic category, priority, source identity, optional
spatial hints, optional region hints, optional structural-context
references even when current channels do not consume all of
them) and explicitly versioned with a preserve-unknown-fields
contract.

**Alignment with existing convention.**
[`CLAUDE.md`](../CLAUDE.md) and
[`docs/SESSION-HANDOFF.md`](../docs/SESSION-HANDOFF.md) define how
Claude Code operates in this repository. The framework extends
those conventions; the implementation sub-stages each update
[`docs/SESSION-HANDOFF.md`](../docs/SESSION-HANDOFF.md) with their
"Where we left off" state and respect the F# / WPF gotchas
documented in [`CONTRIBUTING.md`](../CONTRIBUTING.md).

**The literal-language constraint.** Sight-based metaphors
applied to non-sight phenomena get replaced with concrete
referents in all generated prose: code comments, NVDA
announcements, error strings, log lines, documentation. The
framework's error messages ("Profile load failed: line 14 of
config.toml does not parse as a valid handler entry") follow
this; the framework's TOML schema-checker enforces it for
user-defined entries.

**The linguistic-design rubric** (14 properties from MAY-4.md's
"Note on linguistic design and interaction with dynamic
interfaces" section): *accurate, equivalent, objective,
essential, contextual, common, appropriate, consistent,
unambiguous, clear, concise, understandable, apt, synchronous,
controllable*. The framework recommends these as a checker that
runs at TOML profile-load time, flagging inconsistent vocabulary
across entries, ambiguous templates, and entries that violate the
result-focused property. Implementation deferred to a later
sub-stage; the contract is defined in v1.

**Discovery / Navigation / Selection / On-demand interaction
lifecycle.** Also from MAY-4.md: every dynamic UI element
pty-speak presents to the user (a selection list, a slider, a
streaming-response cursor, any future widget) is structured
around four interaction stages with their own communication
contracts. The framework's `OutputEvent` schema carries a
`VerbosityRegister` field (Approximate / Precise) so the same
content renders at the right register for the user's current
interaction stage.


-----

## Part A — Universal event routing (Concern 1)

### A.1 Architecture overview

Pty-speak's input surface today is a single source — the WPF
keyboard handler in `src/Views/TerminalView.cs` — that delivers
key events to one of three hardcoded sites: the
`KeyEncoding.encodeOrNull` PTY-byte path, the
`HotkeyRegistry.builtIns` hotkey dispatch path (PR-O), or the
`HandleAppLevelShortcut` paste / clear-screen path. Adding a new
source (a foot pedal, an OSC message, a serial line) would
require parallel plumbing through each of those sites; adding a
new handler against an existing event would require code edits
in `Program.fs compose ()`.

The framework introduces three new abstractions between the input
sources and the handler sites:

1. **`RawInput` envelope** — a uniform event type that carries
   the source identity, the source-specific payload, a timestamp,
   and an optional correlation id. Every input source — keyboard
   today, the deferred sources later — emits `RawInput`.
2. **Intent layer** — a registry of named actions (lifting and
   generalising `HotkeyRegistry.AppCommand`) plus a binding table
   that maps `RawInput` patterns to intents. The dispatcher
   consults this table to translate a raw event into an intent.
3. **Dispatcher** — a single named path that runs registered
   pre-stages, the intent's primary handler, and registered
   post-stages. Built-in handlers and user-defined handlers
   register through the same API; the registration mechanism
   emerges from the v1 TOML config and grows to FSI scripts +
   compiled extensions in later phases.

The pipeline:

```
Input source (e.g., WPF KeyEvent)
    ↓
TerminalView/KeyboardSource — translate to RawInput.Keyboard
    ↓
Dispatcher — pre-stages → intent lookup → primary handler → post-stages
    ↓
Effect (e.g., bytes written to PTY, OutputEvent emitted, hotkey action invoked)
```

The substrate the framework lifts (`HotkeyRegistry`,
`KeyEncoding`, `ShellRegistry`) stays largely unchanged; the
framework wires them through the dispatcher rather than
replacing them.

### A.2 The RawInput envelope

```fsharp
namespace Terminal.Core

type InputSource =
    /// The WPF keyboard handler in TerminalView (v1).
    | Keyboard
    /// Reserved for HID device input (foot pedals, switch
    /// interfaces, sip-and-puff). Not implemented in v1; the
    /// case is here so the dispatcher contract stays
    /// source-neutral. Each device-integration project owns its
    /// own research + spec when the maintainer signals priority.
    | Hid
    /// Reserved for OSC (Open Sound Control) messages from
    /// programmable hardware (TouchOSC, MIDI controllers running
    /// OSC bridges). Not implemented in v1.
    | Osc
    /// Reserved for raw serial-line input from Arduino-class
    /// microcontrollers. Not implemented in v1.
    | Serial
    /// Reserved for network-delivered intents (TCP / named-pipe
    /// from external orchestrators). Not implemented in v1.
    | Network
    /// Internal events generated by pty-speak itself
    /// (heartbeat ticks, profile-detection signals, framework
    /// instrumentation). Implemented in v1 for the cases the
    /// framework needs.
    | Internal

type InputPayload =
    /// WPF Key + ModifierKeys, captured at the TerminalView
    /// boundary. The translateHotkeyKey / translateModifiers
    /// helpers in Program.fs (PR-O) translate to the Terminal.Core
    /// representation; the keyboard source emits this case.
    | KeyboardPayload of key: HotkeyRegistry.HotkeyKey * modifiers: Set<HotkeyRegistry.Modifier>
    /// HID-source payload. Reserved; produces failwith in v1.
    | HidPayload of report: byte[]
    /// OSC-source payload. Reserved; produces failwith in v1.
    | OscPayload of address: string * args: obj[]
    /// Serial-source payload. Reserved; produces failwith in v1.
    | SerialPayload of bytes: byte[]
    /// Network-source payload. Reserved; produces failwith in v1.
    | NetworkPayload of source: string * bytes: byte[]
    /// Internal-source payload. The string identifies the
    /// internal producer (e.g., "heartbeat", "profile-switch").
    | InternalPayload of producer: string * detail: obj

type RawInput = {
    Source: InputSource
    Payload: InputPayload
    /// When the event was observed at the source boundary.
    /// Used by handlers that care about input timing (e.g.,
    /// echo-correlation in the Input framework cycle's later
    /// substages).
    Timestamp: System.DateTimeOffset
    /// Optional correlation id linking related events. Used by
    /// future echo-correlation logic (typed character → echoed
    /// character pairing) and future request-response sources.
    /// `None` for v1 keyboard events.
    Correlation: int64 option
}
```

The envelope is intentionally a record (not a struct) because it
travels through async channels; struct-vs-record allocation cost
is dominated by channel-send cost. The `Payload` DU is the
discriminator; downstream handlers pattern-match on `Payload` to
extract source-specific data.

The Keyboard source produces v1's only populated `RawInput`. The
TerminalView keyboard handler, after the existing
`OnPreviewKeyDown` filter chain (app-reserved-hotkey
short-circuit + NVDA-modifier filter + paste/clear-screen
short-circuit), translates the WPF event to `RawInput.Keyboard`
and emits it onto the dispatcher's input channel. The
KeyEncoding path (encoding to PTY bytes) becomes one of the
dispatcher's primary handlers.

### A.3 The Intent layer

```fsharp
type Intent =
    /// All cases from `HotkeyRegistry.AppCommand` (PR-O), plus
    /// the new framework-level intents that emerge as
    /// substages 8a/8b/9a-9d ship.
    /// PR-O cases:
    | CheckForUpdates
    | RunDiagnostic
    | DraftNewRelease
    | OpenLogsFolder
    | CopyLatestLog
    | ToggleDebugLog
    | HealthCheck
    | IncidentMarker
    | SwitchToCmd
    | SwitchToPowerShell
    | SwitchToClaude
    /// New framework intents (each lands in the substage that
    /// owns it; listed here for spec completeness):
    | KillExtensions
    | ReloadConfig
    /// Pass-through intent: the input was not matched to any
    /// registered intent and should be dispatched to the
    /// PTY-byte path. The dispatcher emits this for every
    /// unmatched RawInput.Keyboard so KeyEncoding still runs.
    | PassToPty

type IntentBinding = {
    Intent: Intent
    /// What input gesture triggers this intent. v1 carries
    /// keyboard gestures only. Future substages add HID-report
    /// patterns, OSC-address patterns, serial-line patterns.
    Gesture: InputGesture
    /// Whether the binding is built-in (immutable, ships with
    /// the app) or user-defined (loaded from TOML). Built-in
    /// bindings cannot be removed by user config; user
    /// bindings can override the *gesture* for a built-in
    /// intent but cannot remove the intent itself.
    Origin: BindingOrigin
}

and InputGesture =
    | KeyboardGesture of key: HotkeyRegistry.HotkeyKey * modifiers: Set<HotkeyRegistry.Modifier>
    | HidGesture of report: byte[]      // reserved for v2+
    | OscGesture of address: string     // reserved for v2+
    | SerialGesture of pattern: string  // reserved for v2+

and BindingOrigin =
    | BuiltIn
    | UserConfig of source: string  // path to the TOML file
```

The `IntentRegistry` exposes:

```fsharp
module IntentRegistry =
    /// Built-in intents shipped with the app. Mirrors
    /// HotkeyRegistry.builtIns from PR-O; the rename
    /// HotkeyRegistry → IntentRegistry happens in substage 9b.
    val builtInIntents : Intent list

    /// Built-in default bindings. Mirrors HotkeyRegistry.builtIns
    /// (gesture per intent) from PR-O.
    val builtInBindings : IntentBinding list

    /// Active bindings (built-in + user overrides), populated at
    /// session start by reading TOML config and merging.
    val activeBindings : IntentBinding list ref

    /// Look up the intent for a RawInput.
    val tryRouteIntent : RawInput -> Intent option
```

`tryRouteIntent` returns `Some intent` when the input matches a
registered binding, `None` otherwise. The dispatcher then either
invokes the intent's handler (Some) or emits `Intent.PassToPty`
which routes to KeyEncoding (None).

Key insight: the existing app-reserved-hotkey machinery in
`TerminalView.AppReservedHotkeys` becomes the source of truth for
"which gestures need short-circuit" but the routing decision
moves to the IntentRegistry. The C# table stays as the hot-path
filter (consulted per keystroke in `OnPreviewKeyDown`); the F#
IntentRegistry is consulted at compose-time and at intent
binding lookup time.

### A.4 The Dispatcher

```fsharp
type DispatchResult =
    | Continue       // pre-stage finished; dispatcher proceeds to next pre-stage or primary
    | Replace of Intent  // pre-stage replaced the intent; dispatcher routes to the new intent
    | Cancel          // pre-stage cancelled the dispatch; primary + post-stages skipped
    | Branch of Intent list  // pre-stage spawned additional intents; dispatcher runs all in turn

type DispatchContext = {
    Input: RawInput
    Intent: Intent
    Window: MainWindow  // WPF window for handler access (lazy)
    History: unit -> RawInput list  // recent inputs; lazy to avoid allocation on hot path
    Output: unit -> OutputEvent list  // recent outputs; lazy
    CurrentShell: unit -> ShellRegistry.Shell  // lazy
}

type Dispatcher = {
    /// Pre-stages run before the primary handler. Order is
    /// registration order; each can Continue, Replace, Cancel,
    /// or Branch. Built-in pre-stages: app-reserved-hotkey
    /// short-circuit; NVDA-modifier filter; paste short-circuit.
    PreStages: (DispatchContext -> DispatchResult) list

    /// Primary handlers, one per Intent. Built-in primaries
    /// are the existing run* functions in Program.fs (runUpdateFlow,
    /// runDiagnostic, runOpenNewRelease, runOpenLogs,
    /// runCopyLatestLog, runToggleDebugLog, runHealthCheck,
    /// runIncidentMarker, switchToShell with each Shell, plus the
    /// PassToPty primary that runs KeyEncoding).
    Primaries: Map<Intent, DispatchContext -> unit>

    /// Post-stages run after the primary. Order is registration
    /// order; same DispatchResult semantics as pre-stages.
    /// Built-in post-stages: Heartbeat / instrumentation taps.
    PostStages: (DispatchContext -> DispatchResult) list
}
```

The dispatch loop:

```fsharp
let dispatch (d: Dispatcher) (input: RawInput) : unit =
    let context0 = { Input = input; Intent = ...resolved...; Window = ...; ... }
    // Run pre-stages
    let mutable ctx = context0
    let mutable cancelled = false
    let mutable branchedIntents = []
    for stage in d.PreStages do
        match stage ctx with
        | Continue -> ()
        | Replace newIntent -> ctx <- { ctx with Intent = newIntent }
        | Cancel -> cancelled <- true
        | Branch is -> branchedIntents <- is
    // Run primary unless cancelled
    if not cancelled then
        match Map.tryFind ctx.Intent d.Primaries with
        | Some primary -> primary ctx
        | None -> () // unrecognised intent; logged at Debug
        // Run post-stages
        for stage in d.PostStages do
            stage ctx |> ignore
    // Run any branched intents (each gets its own dispatch cycle)
    for branchedIntent in branchedIntents do
        dispatch d { input with Payload = ...synthetic... }
```

**Performance budget (< 1 ms at 90th percentile, keystroke path).**
The dispatcher is on the hot path; every allocation is suspect.
The lazy `History` / `Output` / `CurrentShell` properties on
`DispatchContext` materialise only on first access, cached for
the dispatch cycle. No pre-stage or post-stage allocates a list
on every keystroke (the Built-in pre-stages early-exit before
allocation when no relevant short-circuit applies).

The dispatcher is single-threaded: it runs on the WPF dispatcher
thread (the same thread that produces the `RawInput.Keyboard`
events). Cross-thread input sources (HID, OSC, serial — all
deferred) marshal their `RawInput` onto the WPF dispatcher
before invoking the dispatcher; the dispatcher itself does not
block on async work.

### A.5 Handler registration paths

Three plausible paths from MAY-4.md, each with its own profile
of cost and capability. The spec commits to ship them in three
phases:

**v1 — Declarative TOML.** Tomlyn (already named in the
deferred-deps list) loads a config file at session start and
populates the `IntentRegistry.activeBindings` ref.

```toml
# Override the default Ctrl+Shift+1 binding for SwitchToCmd
[[bindings]]
intent = "SwitchToCmd"
gesture = "Ctrl+Alt+1"

# A user-defined handler (v1 only knows built-in actions; the
# 'action' field maps to a small repertoire of allowed action
# verbs)
[[handlers]]
intent = "PassToPty"
when = "is_typing_character"   # boolean predicate from a
                                # built-in repertoire
action = "log_keystroke"        # built-in action verb
```

v1's repertoire of allowed `action` verbs is small (log,
announce, suppress, redirect-to-built-in-intent). The TOML schema
includes an explicit version field so older pty-speak versions
can refuse to parse a TOML written for a future schema.

**v2 (Phase 2) — F# Interactive scripts in subprocess sandbox.**
Users drop `.fsx` files into a known directory; pty-speak loads
each script via a subprocess `dotnet fsi` invocation that talks
to pty-speak via JSON-line over stdin/stdout. This avoids the
in-process FSI risks (assembly-load isolation, exception
containment in a screen-reader-critical app) at the cost of IPC
latency. The subprocess sandbox is the explicit choice; in-process
FSI is rejected.

**v3 (Phase 3) — Compiled extension assembly.** Most type-safe;
least dynamic. A formal plugin contract emerges after v1 + v2
reveal what the contract should actually look like. Defer until
demand is real.

### A.6 Kill switch

The kill switch disables all user-defined extensions (TOML
bindings + future FSI scripts + future compiled assemblies),
restoring built-in behaviour.

**Hotkey is canonical.** `Ctrl+Shift+K` (mnemonic: K for Kill).
No current claimant in the AppReservedHotkeys table. Emits an
NVDA announcement on activation: "Extensions disabled. Built-in
behaviour restored. Press Ctrl+Shift+K again to re-enable."
One-session-only by default; persists across the session until
explicitly toggled off.

**Config flag** — `extensibility.killSwitch = true` in the TOML
config persists the kill across sessions. Useful for
unattended-deployment / safety scenarios.

**CLI flag** — `--kill-extensions` on the command line is
per-process. Useful for one-off launches without touching config.

The hotkey + config + CLI all set the same in-memory flag; reading
that flag gates the Intent dispatch table (when set, the
dispatcher uses `IntentRegistry.builtInBindings` instead of
`activeBindings`).

### A.7 Forward-looking input sources

The InputSource DU includes Hid / Osc / Serial / Network cases
in v1 even though only Keyboard is populated. Adding a new source
in a future stage is:

1. Implement the source-side adapter (e.g., `HidSource.fs`
   reading Windows Raw Input API).
2. Translate the source's events to `RawInput.Hid` (or the
   relevant case) at the source boundary.
3. Marshal to the WPF dispatcher thread.
4. Invoke the existing dispatcher.

No changes to `Dispatcher`, `IntentRegistry`, `tryRouteIntent`, or
any handler. The intent layer's `InputGesture` DU also has the
non-Keyboard cases reserved; the binding table accepts them when
the source ships.

This is the core "extensibility for as many input mechanisms as
possible" the maintainer asked for, captured at the abstraction
level rather than the implementation level.

## Part B — Output framework (Concern 2)

### B.1 Architecture overview

The output framework converts the existing single-channel pipeline
(ConPTY → VtParser → SemanticMapper → ScreenNotification →
Coalescer → drainTask → NVDA `RaiseNotificationEvent`) into a
three-stage substrate that admits multiple channels:

```
SemanticMapper / Coalescer / SelectionDetector / ParserError-emitter
                          ↓
                    OutputEvent (typed, versioned, metadata-rich)
                          ↓
                Profile-set (per-shell: e.g. ["stream", "selection"])
                          ↓
            ChannelDecision[] (one per channel that wants this event)
                          ↓
        ┌─────────────────┼─────────────────┐
        ↓                 ↓                 ↓
   NVDA channel    FileLogger channel   WASAPI Earcons channel
   (UIA Notif.)    (rolling log file)   (NAudio palette)
```

Compared to the current single-channel pipeline:

- **Producers** (the things that emit OutputEvents) are the
  existing semantic-recognising components: `Coalescer.append /
  drain` (already produces announcement strings; reframed as
  `StreamChunk` OutputEvents), the Stage 8 selection-detection
  heuristic (lifts to `SelectionShown` / `SelectionItem` events),
  the parser-error path (emits `ParserError` events), the
  hyperlink / bell / alt-screen / mode-barrier paths.
- **Profiles** are pure functions `OutputEvent → ChannelDecision[]`
  with per-instance state. A profile is the policy: "given this
  OutputEvent, which channels should render it, and in what form?"
- **Channels** are the destinations. Each channel has its own
  rendering logic, threading model, and ordering guarantees.

The framework is deliberately a fan-out: one OutputEvent can route
to zero, one, or many channels depending on the active profile-set.
This is what "abstracted so other output mechanisms can choose what
to do with it" means in concrete substrate terms.

### B.2 The OutputEvent schema

#### B.2.1 Position

The OutputEvent type is a record with explicit `Version` field and
an `Extensions: Map<string, obj>` for forward-compatible round-trip.
MAY-4.md's cross-cutting recommendation: be generous with metadata
at emission time, even when the current set of channels does not
consume all of it.

#### B.2.2 Schema (v1)

```fsharp
namespace Terminal.Core

[<RequireQualifiedAccess>]
type SemanticCategory =
    | StreamChunk          // Coalesced text from the running shell
    | SelectionShown       // A selection prompt has appeared
    | SelectionItem        // A single item within a selection
    | SelectionDismissed   // Selection prompt has been resolved
    | SpinnerTick          // Spinner frame from the shell (often
                           //   suppressed at profile layer)
    | ErrorLine            // Stderr-flagged or red-coloured line
    | WarningLine          // Yellow / amber line
    | PromptDetected       // Shell prompt redrawn (PS1 / PROMPT)
    | CommandSubmitted     // User submitted a command (Enter)
    | BellRang             // BEL (0x07) received
    | HyperlinkOpened      // OSC 8 hyperlink emitted
    | AltScreenEntered     // DECSET 1049
    | ModeBarrier          // Other mode-flip barrier event
    | ParserError          // VtParser detected malformed sequence
    | Custom of string     // User-defined; maps via Extensions

[<RequireQualifiedAccess>]
type Priority =
    | Interrupt    // Stops current speech; speaks immediately
    | Assertive    // Queued ahead of polite items
    | Polite       // Standard queue behaviour
    | Background   // Suppressed at profile layer for screen
                   //   readers; may render on other channels

[<RequireQualifiedAccess>]
type VerbosityRegister =
    | Approximate  // "Yes, 2 of 4"
    | Precise      // Full literal text

type SourceIdentity = {
    Producer: string                // "coalescer" / "selection-detector" / ...
    Shell: ShellId option           // Current shell identity (PR-B)
    CorrelationId: int64 option     // Links related events (e.g.
                                    //   a SelectionShown +
                                    //   subsequent SelectionItems)
}

type SpatialHint = {
    Azimuth: float    // -180 to +180 (degrees; 0 = front, +90 = right)
    Elevation: float  // -90 to +90 (degrees; 0 = horizon)
    Distance: float   // 0..1 (relative; consumed by spatial engines)
}

type RegionHint = {
    NamedRegion: string  // "header" / "body" / "footer" / arbitrary
    Order: int           // Within-region ordering hint
}

type StructuralRef = {
    ParentSegmentId: int64    // Concern 3 streaming-queue navigation
    OrderInParent: int
}

type RenderInstruction =
    | RenderText of string
    | RenderText2 of approx: string * precise: string
    | RenderEarcon of EarconId
    | RenderRaw of payload: obj   // Channel-specific opaque payload

type ChannelDecision = {
    Channel: ChannelId
    Render: RenderInstruction
}

type OutputEvent = {
    Semantic: SemanticCategory
    Priority: Priority
    Verbosity: VerbosityRegister
    Source: SourceIdentity
    SpatialHint: SpatialHint option
    RegionHint: RegionHint option
    StructuralContext: StructuralRef option
    Payload: string                  // The literal content the
                                     //   event carries; may be
                                     //   empty for events that
                                     //   are pure signals (BellRang)
    Version: int                     // v1 = 1
    Extensions: Map<string, obj>     // Round-tripped unknown fields
}
```

#### B.2.3 Why this shape

- **`Semantic` as a closed DU + `Custom of string` escape hatch.**
  v1 ships the 14 enumerated cases. User-defined event categories
  go through `Custom` and consult `Extensions` for metadata. This
  is the same shape as `InputSource` and `Intent`: closed DU for
  the canonical set, string-keyed escape hatch for extensibility.
- **`Priority` orthogonal to `Semantic`.** A `StreamChunk` can be
  Polite (steady stream) or Interrupt (user pressed Ctrl+C and the
  next chunk is the cancellation message). Priority is computed by
  the producer, not by the profile.
- **`Verbosity` in the schema.** The MAY-4.md linguistic-design
  framework's Discovery / Navigation / Selection / On-demand
  lifecycle implies the same content rendered at different
  registers. Carrying register in the schema means the profile can
  emit `RenderText2 of approx, precise` and the channel picks the
  matching one based on user state without re-querying.
- **`SpatialHint` / `RegionHint` / `StructuralContext` as
  optionals.** Per MAY-4.md: be generous at emission time. v1
  channels (NVDA / FileLogger / Earcons) consume none of these. v3
  channels (spatial audio / multi-line braille / streaming-queue
  navigation) consume them when they ship. The optionals are `None`
  on the hot path; allocation cost is one option-tag per event.
- **`Version` + `Extensions`.** A profile entry written for a
  future device (v3 Monarch braille rendering instruction) survives
  round-trip through an older pty-speak that doesn't yet know about
  it. The `Extensions` map preserves the unknown fields; the future
  channel reads them.
- **`Payload: string`.** The literal text the event carries. For
  signal-only events (BellRang, AltScreenEntered) it's the empty
  string. For text-bearing events it's the canonical sanitised
  text post-`AnnounceSanitiser` (the PR-N contract is preserved).

#### B.2.4 Producer responsibilities

A producer (Coalescer, SelectionDetector, etc.) MUST:

1. Set `Semantic` from the closed DU (or `Custom` with a stable
   string ID).
2. Set `Priority` based on the event's nature (StreamChunk default
   Polite; ErrorLine default Assertive; ParserError default
   Background; BellRang default Assertive; etc.).
3. Set `Verbosity` to Precise unless the producer is an
   approximate-mode emitter (none in v1).
4. Set `Source.Producer` to a stable identifier ("coalescer",
   "selection-detector", "vt-parser") and `Source.Shell` to the
   current shell from `ShellRegistry`.
5. Run sanitisation through `AnnounceSanitiser.sanitise` BEFORE
   placing the text in `Payload`. The PR-N contract is the entry
   gate to the OutputEvent substrate.
6. Set `Version = 1` and `Extensions = Map.empty`.
7. Leave `SpatialHint`, `RegionHint`, `StructuralContext` as `None`
   in v1 (no producer in v1 has the metadata to populate them).

### B.3 Profile abstraction

#### B.3.1 Position

A Profile is a function `OutputEvent -> ChannelDecision[]` with
per-instance state. Each profile is constructed with caller-supplied
parameters; the constants the existing Coalescer carries
(debounce window, spinner threshold, max announcement length)
become per-instance parameters. This completes what the PR-N
docstring contract anticipated: "one Stream profile is what the
current Coalescer is".

#### B.3.2 Profiles in v1

| Profile | What it does | Replaces / Retrofits |
|---|---|---|
| **Stream** | Coalesces high-frequency StreamChunk events; suppresses spinner storms; truncates over `maxAnnounceChars`. Emits `RenderText` to the NVDA channel + raw `Payload` to the FileLogger channel. | The current `Coalescer.fs` becomes a Stream-profile instance. |
| **Selection** | Recognises `SelectionShown` / `SelectionItem` / `SelectionDismissed` events; emits NVDA-channel `RenderRaw` payloads carrying UIA listbox metadata; emits FileLogger-channel `RenderText` summary lines. | Replaces tech-plan §8 (Interactive list detection + UIA List provider). The detection heuristic from §8.1 is preserved at the producer (a new `SelectionDetector.fs`); the UIA listbox semantics emit through the profile rather than a parallel UIA subtree. |
| **Earcon** | Maps OutputEvent.Semantic to earcon IDs; emits NVDA-channel suppression for events the earcon channel claims (so a colour-mapped earcon doesn't double-up with NVDA reading the colour name). | Replaces tech-plan §9 (Earcons via NAudio). The NAudio palette + frequency mapping from §9.3 carry forward; the channel + profile abstractions wrap them. |

Profiles **deferred to Phase 2** (their `Semantic` cases exist in
v1 so future profiles slot in without OutputEvent changes):

- **Form profile** — for shell-side form widgets (gum form,
  Inquirer.js prompts). Recognises field labels, current focus,
  validation messages.
- **TUI profile** — for full-screen TUI applications (less, vim,
  htop). Recognises mode lines, status bars, scrollable regions.
- **REPL profile** — for interactive REPLs (Python, Node, IEx).
  Recognises prompt-vs-output regions, multi-line input continuation.

#### B.3.3 Profile signature

```fsharp
type Profile = {
    Id: string                              // "stream" / "selection" / ...
    Apply: OutputEvent -> ChannelDecision[]
    Reset: unit -> unit                     // Called when shell switches
}

module StreamProfile =
    type Parameters = {
        DebounceWindowMs: int      // PR-N: was module global
        SpinnerWindowMs: int       // PR-N: was module global
        SpinnerThreshold: int      // PR-N: was module global
        MaxAnnounceChars: int      // PR-H stopgap
    }

    val create: Parameters -> Profile

module SelectionProfile =
    type Parameters = {
        HighlightDetectionThresholdMs: int   // From §8.1
    }

    val create: Parameters -> Profile

module EarconProfile =
    type Parameters = {
        ColourMappingEnabled: bool
        SemanticEarconMap: Map<SemanticCategory, EarconId>
    }

    val create: Parameters -> Profile
```

#### B.3.4 User-facing TOML schema (v1)

```toml
[profile.stream]
debounceWindowMs = 200
spinnerWindowMs = 1000
spinnerThreshold = 5
maxAnnounceChars = 500

[profile.selection]
highlightDetectionThresholdMs = 100

[profile.earcon]
colourMappingEnabled = true

[profile.earcon.semanticMap]
ErrorLine = "error-tone"
WarningLine = "warning-tone"
BellRang = "bell-ping"

[shell.cmd]
profiles = ["stream"]

[shell.claude]
profiles = ["stream", "selection"]

[shell.powershell]
profiles = ["stream"]
```

#### B.3.5 Why this shape

- **Profile-as-function.** Composable: chaining profiles is just
  applying them in order and concatenating their ChannelDecisions.
  Per-instance state (the Coalescer's debounce buffer, the
  SelectionDetector's highlight history) lives in the closure
  the constructor builds, not in module globals — which is what
  PR-N's docstring asked for.
- **Per-shell mapping mirrors `ShellRegistry`.** The existing
  pattern is: `ShellRegistry.builtIns` is a list of records, the
  TOML can override entries by ID. Profiles follow the same shape:
  `ProfileRegistry.builtIns` for Stream / Selection / Earcon;
  TOML overrides a profile's parameters or assigns a profile-set
  per shell.
- **One flat profile-set per shell in v1.** Profile composition
  (a base + an overlay + per-session overrides) is MAY-4.md's
  Concern-2 question 5. Deferred to Phase 2 if the maintainer
  wants it; the v1 substrate handles flat sets cleanly.

### B.4 Channel surface

#### B.4.1 Position

Three channels in v1 (NVDA + FileLogger + WASAPI Earcons). The
deferred channels (JAWS, Narrator, Piper, plain stdout, spatial
audio, multi-line braille, network fan-out) are framed in the
schema (priority taxonomy table reserves their rows; OutputEvent
metadata admits them via `SpatialHint` / `RegionHint` /
`Extensions`) but not implemented.

#### B.4.2 v1 channels

**NVDA channel.** The current path. Routes from `OutputEvent`
through the existing `AnnounceSanitiser + ActivityIds` pairing
(the PR-N contract). Implementation:

```fsharp
module NvdaChannel =
    type Parameters = {
        Provider: ITerminalDocumentProvider   // Existing UIA provider
    }

    val create: Parameters -> Channel

    // Channel.send signature:
    // OutputEvent + RenderInstruction → unit
    // (raises UIA NotificationEvent with Priority-mapped
    //  NotificationProcessing kind; see B.5)
```

The NVDA channel reads `Priority` from the OutputEvent and maps
it to `NotificationProcessing` per the table in B.5. For
`Background` priority it suppresses (no UIA notification raised).

**FileLogger channel.** `Terminal.Core.FileLogger` is promoted to
first-class channel. Today it logs strings via `Information` /
`Debug` levels; post-retrofit it consumes OutputEvents and writes
structured records:

```
2026-05-15T14:32:01.123Z [Information] OutputEvent
  semantic=StreamChunk priority=Polite verbosity=Precise
  source=coalescer shell=cmd
  payload="ls -la output line goes here"
```

This naturally captures everything the user heard. The
`Ctrl+Shift+;` clipboard-copy flow (PR-F) carries the full event
trail for post-hoc diagnosis.

**WASAPI Earcons channel.** New in this cycle. Replaces tech-plan
§9.

```fsharp
module EarconChannel =
    type Parameters = {
        WasapiDevice: WasapiDevice
        Palette: Map<EarconId, EarconWaveform>
        VolumeDb: float
    }

    val create: Parameters -> Channel
```

The NAudio palette from §9.3 is the v1 default `Palette`. The
EarconChannel renders `RenderEarcon of EarconId` instructions by
playing the corresponding waveform on the WasapiDevice. The
channel respects per-event mute state (Ctrl+Shift+M reserved
hotkey) and per-shell mute state (TOML `[shell.X]
muteEarcons = true`).

#### B.4.3 Deferred channels (Phase 2)

| Channel | Why Phase 2 | Substrate already in place |
|---|---|---|
| **JAWS** | Same UIA surface as NVDA; per-screen-reader profile entries override NVDA defaults. Validated when the maintainer has JAWS-on-Windows test access. | `RaiseNotificationEvent` + Priority taxonomy. |
| **Narrator** | Same UIA surface, separate validation. | Ditto. |
| **Piper TTS** | Subprocess; GPL-3 license boundary documented; useful for sighted users / SSH / `--no-speech` falls back to it. | RenderText instructions are TTS-ready. |
| **plain stdout** | For SSH / CI / `--no-speech`. Plain text writes of the event payload + a category prefix. | Trivial implementation; substrate is the Channel record. |

#### B.4.4 Deferred channels (Phase 3)

| Channel | Why Phase 3 | Substrate already in place |
|---|---|---|
| **Spatial audio** | Subprocess to SuperCollider or OpenAL Soft / Steam Audio. Renders earcons + speech with positional metadata. | OutputEvent.SpatialHint already carries azimuth / elevation / distance. |
| **Multi-line refreshable braille** | Monarch, Dot Pad, future devices. Renders structural OutputEvent regions onto a 2D braille display. | OutputEvent.RegionHint already carries NamedRegion + Order. |
| **Network fan-out** | TCP / named-pipe to external diagnostic tools. | Channel record permits an `INetworkSink` implementation. |

### B.5 Threading + priority taxonomy

#### B.5.1 Position

Four-level priority `Interrupt | Assertive | Polite | Background`.
Per-screen-reader UIA mapping table pinned for NVDA;
"validated when X ships" labels for JAWS / Narrator. Per-channel
order preservation by default; cross-channel parallelism allowed.
Threading primitive reuses `System.Threading.Channels` — already
standardised in this codebase (Coalescer, ConPtyHost).

#### B.5.2 Priority → UIA NotificationProcessing mapping

| Priority | NVDA | JAWS | Narrator |
|---|---|---|---|
| Interrupt | `ImportantMostRecent` | _validated when JAWS channel ships_ | _validated when Narrator channel ships_ |
| Assertive | `ImportantAll` | _ditto_ | _ditto_ |
| Polite | `All` | _ditto_ | _ditto_ |
| Background | _suppressed at profile layer; never emitted as UIA notification_ | _ditto_ | _ditto_ |

NVDA mappings match the `ImportantAll` / `ImportantMostRecent`
split shipped in PR #100. JAWS / Narrator rows are deliberately
honest about what the framework does not yet promise.

#### B.5.3 Threading model

- **Per-channel order preservation by default.** A channel's
  `send` operations execute in the order the dispatcher submitted
  them. NVDA channel uses the existing single-threaded drain
  (PR-N's contract). FileLogger channel uses the existing
  `System.Threading.Channels` writer pattern.
- **Cross-channel parallelism.** A single OutputEvent's
  ChannelDecisions are dispatched in parallel — NVDA + FileLogger
  + Earcon all see the event simultaneously. No channel waits on
  another.
- **Profile-layer suppression for cross-channel deduplication.**
  When the Earcon profile claims a `BellRang` event (channels =
  Earcon only), it adds an NVDA-channel suppression entry to the
  ChannelDecision array, so NVDA doesn't double-up by reading
  "bell".
- **Backpressure.** A channel that falls behind (FileLogger disk
  IO, Earcon WASAPI buffer) drops with a logged warning rather
  than blocking the dispatcher. The hot path stays under the
  < 1 ms keystroke budget from A.4.4 even when channels misbehave.

#### B.5.4 Dispatcher pseudocode

```fsharp
let dispatch (event: OutputEvent) (profileSet: Profile list) : unit =
    let decisions =
        profileSet
        |> List.collect (fun p -> p.Apply event |> Array.toList)
    let suppressedChannels =
        decisions
        |> List.choose (fun d ->
            match d.Render with
            | RenderRaw payload when isSuppression payload ->
                Some d.Channel
            | _ -> None)
        |> Set.ofList
    decisions
    |> List.filter (fun d -> not (Set.contains d.Channel suppressedChannels))
    |> List.iter (fun decision ->
        let ch = ChannelRegistry.lookup decision.Channel
        ch.Send(event, decision.Render))
```

The dispatch is fire-and-forget per channel; each channel internally
queues via `System.Threading.Channels` and drains on its own
worker.

### B.6 Profile detection

#### B.6.1 Position

Explicit per-shell mapping in v1; heuristic detection deferred.
When the user switches shells via `Ctrl+Shift+1/2/3`, the new
shell's profile-set is loaded from TOML.

#### B.6.2 Mechanism

```fsharp
// In ShellRegistry.fs (existing) we add:
type ShellEntry = {
    Id: ShellId
    DisplayName: string
    LaunchArgs: LaunchArgs
    Profiles: string list   // NEW: profile IDs from ProfileRegistry
}

// On switchToShell:
let switchToShell (shellId: ShellId) : unit =
    let entry = ShellRegistry.lookup shellId
    activeProfileSet <- entry.Profiles |> List.map ProfileRegistry.lookup
    activeProfileSet |> List.iter (fun p -> p.Reset())
    // Existing PTY swap continues
    PtyHost.swap entry.LaunchArgs
```

#### B.6.3 Inner-shell limitation

If the user types `pwsh` inside a `cmd` shell, pty-speak only
sees the outer `cmd` shell it spawned via ConPTY. The cmd
profile-set stays active. This is documented as a known
limitation; framework behaves correctly when the user explicitly
switches via `Ctrl+Shift+1/2/3`. Heuristic inner-shell detection
(spotting `pwsh` text in the prompt, watching for known prompt
patterns) is research work in its own right per MAY-4.md and is
out of scope here.

### B.7 Verbosity registers

#### B.7.1 Position

`VerbosityRegister = Approximate | Precise` is in the OutputEvent
schema. v1 Stream profile only emits `Precise`; the Approximate
register is wired through the dispatch path but underused until
later stages (Concern 3 + Stage 10's review-mode quick-nav) wire
the user-side switch.

#### B.7.2 Why in the schema, not the profile

MAY-4.md's Discovery / Navigation / Selection / On-demand
lifecycle implies the same content is rendered at different
registers depending on user interaction stage:

- Discovery: Approximate ("you have a selection prompt with 4
  items").
- Navigation: Precise on focus, Approximate on flyover.
- Selection: Precise.
- On-demand: Precise.

If the register were a profile-layer concern, the profile would
need to query user-state (current stage in the lifecycle) on
every event, which couples profiles to UI state. Carrying the
register in the event lets the producer emit at the natural
register and the channel render the right one based on context.

#### B.7.3 Producer guidance

Producers should emit **both registers** when the difference is
meaningful, using `RenderInstruction.RenderText2`:

```fsharp
RenderText2(
    approx = "selection prompt, 4 items",
    precise = "Selection prompt: 1) Yes — confirms the edit and
              proceeds; 2) No — cancels the edit; 3) Edit further
              before applying; 4) Discard")
```

The channel selects which to render based on its own user-state
awareness (NVDA channel: respects user's verbosity hotkey;
FileLogger channel: always emits Precise).

## Part C — Sub-stage breakdown

The spec is the substrate; landing it across stages follows the
walking-skeleton discipline (CLAUDE.md). Each sub-stage is
independently NVDA-validatable, ships a single-concern PR, and
satisfies the cross-cutting commitments in the Design Principles
section.

### C.1 Output framework cycle (replaces tech-plan §8 + §9)

| Stage | What it ships | NVDA-validation row |
|---|---|---|
| **8a** — OutputEvent + Channel + NVDA-channel retrofit | New types in `Terminal.Core`; existing `ScreenNotification → Coalescer → drainTask → Announce` rerouted through the framework, behaviour-identical. The dispatcher runs but with one profile (Stream) and one channel (NVDA). | "Type fast in cmd; verify identical to pre-retrofit; no regressions in Stage-5 streaming validation rows." |
| **8b** — Stream profile (Coalescer becomes a Profile instance) | `Profile` abstraction; Coalescer's per-instance state wired through; constants become per-instance parameters per the PR-N docstring contract. TOML loader respects `[profile.stream]` parameters but does not yet swap profile-sets per shell. | "All Stage-5 streaming validation rows still pass; rebind `debounceWindowMs` via TOML and verify the change after restart." |
| **8c** — FileLogger as first-class channel | FileLogger consumes OutputEvents (not just log strings). The `Ctrl+Shift+;` flow naturally captures everything the user heard, structured. | "Reproduce a session; verify log captures match announcements; verify ParserError events appear in log even when NVDA channel suppressed them." |
| **8d** — WASAPI Earcons channel + Earcon profile | NAudio palette from §9.3; Earcon profile maps `OutputEvent.Semantic` to earcon. Replaces tech-plan §9. | "Run colour-emitting commands; hear earcons; verify Ctrl+Shift+M mute hotkey works; verify earcon-claimed events suppress NVDA-channel double-up." |
| **8e** — Selection profile | §8 detection heuristic lifts to a Profile that emits `SelectionShown` / `SelectionItem` / `SelectionDismissed` OutputEvents. NVDA channel renders these as a UIA List per §8.2. | "Trigger a Claude Code prompt; verify listbox semantics; verify arrow-key navigation announces items at appropriate verbosity register." |
| **8f** — Per-shell profile mapping via ShellRegistry | TOML `[shell.X] profiles = [...]` schema; `switchToShell` loads the new shell's profile-set. | "Switch shells with Ctrl+Shift+1/2/3; verify profiles change; verify shell-specific muteEarcons works." |

### C.2 Input framework cycle (generalises HotkeyRegistry)

| Stage | What it ships | NVDA-validation row |
|---|---|---|
| **9a** — RawInput envelope + Keyboard source | New types; TerminalView keyboard handler emits `RawInput.Keyboard`. Dispatcher consumes RawInput; existing hotkey machinery wraps the new layer. | "All shipped hotkeys still fire; verify diagnostic snapshot via Ctrl+Shift+H still announces." |
| **9b** — Intent layer + IntentRegistry | `HotkeyRegistry` → `IntentRegistry` rename (in code); `AppCommand` → `Intent` rename; `IntentBinding` records replace direct WPF-key dispatch. | "All hotkeys still bind via the new registry; pinned-fixture test passes." |
| **9c** — TOML declarative handler registration | Tomlyn integration; `[bindings]` and `[handlers]` sections; load-once at session start. | "Override one binding via TOML (e.g., move `Ctrl+Shift+H` to `Ctrl+Shift+J`); verify it takes effect post-restart; verify duplicate-binding load-time error announces clearly." |
| **9d** — Kill switch (hotkey + config + CLI) | `Ctrl+Shift+K` Intent + `extensibility.killSwitch` config + `--kill-extensions` CLI. | "Press hotkey; verify all user-defined extensions disabled, built-ins still work; verify `Ctrl+Shift+H` announces kill-switch state." |

### C.3 Stage 10 (review mode) — reframed

Stage 10 stays a feature stage but reframed: the quick-nav letters
(`e`/`w`/`p`/`c`/`o`/`i`) become handlers registered against
Selection-profile OutputEvents (when matched against the appropriate
Semantic categories). The mode-toggle hotkey (`Alt+Shift+R`) is an
Intent in the new IntentRegistry. Color-based detection from §10.3
stays as the v1 implementation; semantic-richer detection is the
natural Concern-3-spec extension.

### C.4 Spec text supersession in tech-plan.md

The new spec doesn't delete §8 / §9 / §10 from tech-plan.md.
Instead:

- §8 gets a one-paragraph **"Superseded by `spec/event-and-output-framework.md`"** header preserving the original content as historical reference. The May-2026 plan + this spec are the active sources.
- §9 ditto.
- §10 gets a one-paragraph **"Reframed as the first non-built-in framework consumer"** header pointing at the new spec for the substrate it builds on. Original §10 content stays as the feature plan.

## Part D — Retrofit specifics

### D.1 How the existing pipeline becomes the Stream profile

The current pipeline:

```
ConPtyHost → PipeReader → VtParser → SemanticMapper
    → ScreenNotification → Coalescer.append
    → Coalescer drain → ITerminalDocumentProvider.Announce
```

becomes, post-retrofit:

```
ConPtyHost → PipeReader → VtParser → SemanticMapper
    → ScreenNotification → toOutputEvent
    → Dispatcher
        → ProfileSet.Apply (currently [Stream])
            → Stream.Apply: coalesce, debounce, spinner-suppress
            → Returns ChannelDecision[] = [
                { Channel = NvdaChannel; Render = RenderText "..." }
                { Channel = FileLogger; Render = RenderText "..." }
              ]
    → ChannelRegistry.dispatch
        → NvdaChannel.send → ITerminalDocumentProvider.Announce
        → FileLogger.send → file write
```

The translation `ScreenNotification → toOutputEvent` happens at
the boundary between the existing parser pipeline and the new
framework. It's a function in `Terminal.Core/SemanticMapper.fs`
(or a new `OutputEventBuilder.fs`) that:

1. Reads `ScreenNotification` (e.g., `RowsChanged of int list`).
2. Builds the corresponding `OutputEvent`:
   - `Semantic = StreamChunk` for typical row updates
   - `Semantic = ParserError` for `ParserError` notifications
   - `Semantic = AltScreenEntered` for `ModeChanged(AltScreen, true)`
   - etc.
3. Sets `Source.Producer = "semantic-mapper"`.
4. Sets `Source.Shell` from `ShellRegistry.activeShell`.
5. Computes `Priority` per producer guidance (B.2.4 step 2).
6. Sets `Verbosity = Precise`.
7. Sets `Payload` from the existing string the Coalescer would
   have built (which already passes through `AnnounceSanitiser`).

The Coalescer's debounce / spinner-suppress / max-announce-chars
logic moves into `StreamProfile.Apply`. The drain task is replaced
by the dispatcher's per-channel queue drains.

### D.2 ScreenNotification → OutputEvent mapping table

| ScreenNotification case | OutputEvent.Semantic | Default Priority |
|---|---|---|
| `RowsChanged of rows` | `StreamChunk` | Polite |
| `ParserError of msg` | `ParserError` | Background |
| `ModeChanged(AltScreen, true)` | `AltScreenEntered` | Assertive |
| `ModeChanged(AltScreen, false)` | `ModeBarrier` | Assertive |
| `ModeChanged(BracketedPaste, _)` | `ModeBarrier` | Polite |
| `ModeChanged(_, _)` (other) | `ModeBarrier` | Polite |

Producers added in 8d/8e (selection detector, bell observer,
hyperlink observer) emit OutputEvents directly without going
through ScreenNotification.

### D.3 HotkeyRegistry → IntentRegistry rename

The rename is mechanical. Every site that names `AppCommand`
or `HotkeyRegistry` updates to `Intent` / `IntentRegistry`. The
pinned-fixture tests from PR-O lock the binding mapping; updating
the test file's type names (without changing the asserted bindings)
is the test side of the rename.

Files touched (from a `git grep AppCommand` baseline):

- `src/Terminal.Core/HotkeyRegistry.fs` → renamed to `IntentRegistry.fs`
- `src/Terminal.App/Program.fs` (the dispatch site)
- `src/Views/TerminalView.cs` (the docstring + AppReservedHotkeys
  table)
- `tests/Tests.Unit/HotkeyRegistryTests.fs` → renamed to
  `IntentRegistryTests.fs`
- `tests/Tests.Unit/Tests.Unit.fsproj` (the `<Compile Include>`
  entry for the renamed test file)

The rename PR (Stage 9b) ships ONLY the rename — no semantic
changes, no new bindings, no new tests. CI green is the
acceptance gate.

### D.4 The Coalescer-becomes-Profile ratification

PR-N added per-shell-instance Coalescer state and a docstring
contract. Stage 8b promotes that contract to a profile:

- `Coalescer.fs` becomes `StreamProfile.fs`.
- The constants (debounce window 200 ms, spinner window 1000 ms,
  spinner threshold 5, max announce chars 500) become
  `StreamProfile.Parameters` fields.
- The construction site (`new Coalescer(...)` per shell session)
  becomes `StreamProfile.create parameters`.
- `Coalescer.append` becomes the producer-side translation
  (`SemanticMapper.toOutputEvent`).
- `Coalescer.drain` becomes the dispatcher's per-channel drain.

The signature change is mechanical. The behaviour change is zero
when the v1 `StreamProfile.Parameters` defaults match the current
constants — which is the explicit requirement of stage 8b.

## Part E — Out of scope, verification, open questions

### E.1 Things deliberately out of scope

- **Concern 3 (navigable streaming response queue).** Its own
  spec when Stage 10 starts. The framework substrate this spec
  defines is the prerequisite (`StructuralRef` is reserved in the
  OutputEvent schema for that work).
- **Implementation of any sub-stage.** This spec is a doc, not
  code. Sub-stages 8a–8f and 9a–9d each become their own future
  PR with their own NVDA validation cycle.
- **HID / OSC / MIDI / serial input source implementations.** The
  `InputSource` DU includes the cases; their device-integration
  projects each get their own research + spec when the maintainer
  signals priority. No transport decisions made now.
- **Spatial audio / multi-line braille / network fan-out channel
  implementations.** The OutputEvent schema admits them via
  `SpatialHint` / `RegionHint` / `Extensions`; their channel
  implementations are Phase 3.
- **F# Interactive script extension path.** Phase 2 with the
  subprocess sandbox approach; the spec describes the contract
  but doesn't ship it.
- **Compiled extension assembly contract.** Phase 3.
- **Profile composition** (base + overlay + session overrides).
  Phase 2 if the maintainer wants it; v1 has flat per-shell
  profile-sets.
- **Heuristic profile detection.** Research project of its own;
  v1 is shell-driven only via `ShellRegistry`.
- **"Explain why pty-speak said X" command.** Phase 2 if the
  existing FileLogger diagnostic flow proves insufficient.
- **Hot-reload of TOML config.** Phase 2; v1 reads once at
  session start. Restart is the v1 reload path.
- **JAWS + Narrator UIA `NotificationProcessing` mapping table
  entries.** Pinned when those channels ship; v1 is "validated
  when X ships" labels.
- **Linguistic-design rubric automated checker.** The 14
  properties from MAY-4.md become a load-time checker for TOML
  profile entries in a later sub-stage; the contract is defined
  in v1, the implementation is deferred.

### E.2 Critical files this spec implies

The spec PR itself touches only documentation. Implementation
PRs (one per sub-stage) will touch the file groups below.

**Spec PR (this one):**

- `spec/event-and-output-framework.md` (NEW — this file)
- `spec/tech-plan.md` — §8 / §9 / §10 section headers updated
  with supersession / reframe notices (~10 lines each, content
  preserved)
- `docs/PROJECT-PLAN-2026-05.md` — Part 3 + Part 4
  cross-reference the new spec as the produced deliverable
- `docs/DOC-MAP.md` — new row for the spec file under the
  audience table
- `docs/SESSION-HANDOFF.md` — "Where we left off" updated;
  "Next stage" reframed
- `CHANGELOG.md` — `[Unreleased] ### Documentation` entry

**Subsequent implementation stages (file groups, not exhaustive):**

- Stage 8a: `src/Terminal.Core/Types.fs` (add OutputEvent +
  Channel + ChannelDecision types), `src/Terminal.Core/SemanticMapper.fs`
  (add `toOutputEvent`), `src/Terminal.Core/ChannelRegistry.fs`
  (NEW), `src/Terminal.Core/NvdaChannel.fs` (NEW), removal of
  direct `Announce` calls from `Program.fs` in favour of dispatcher
- Stage 8b: `src/Terminal.Core/Coalescer.fs` →
  `src/Terminal.Core/StreamProfile.fs` rename + per-instance
  parameter refactor; `src/Terminal.Core/ProfileRegistry.fs`
  (NEW)
- Stage 8c: `src/Terminal.Core/FileLogger.fs` (extend to consume
  OutputEvent records)
- Stage 8d: new `src/Terminal.Core/EarconChannel.fs` +
  `src/Terminal.Core/EarconProfile.fs`; NAudio dependency added
  to `Terminal.Core.fsproj`
- Stage 8e: new `src/Terminal.Core/SelectionDetector.fs` +
  `src/Terminal.Core/SelectionProfile.fs`; NVDA channel learns
  to render `RenderRaw` payloads as UIA listbox semantics
- Stage 8f: `src/Terminal.Pty/ShellRegistry.fs` (extend
  `ShellEntry` with `Profiles: string list`); TOML loader added
- Stage 9a-9d: file paths in D.3

### E.3 Existing patterns to reuse

- **`spec/tech-plan.md` §-numbered stage format** — this spec
  mirrors it for sub-stages (8a / 8b / 9a / 9b labelling).
- **`spec/overview.md` framing rhythm** (problem → prior art →
  architectural commitment → sub-questions / risks) — applies to
  each Concern in this spec.
- **`docs/research/MAY-4.md`** consolidated questions list —
  drove the "Decisions committed" structure of this spec.
- **`docs/STAGE-7-ISSUES.md`** framework-input mappings (added
  in PR #149) — the sub-stage breakdown above mirrors that
  taxonomy.
- **`docs/USER-SETTINGS.md`** "Current state / Why hardcoded now
  / What configurability would look like / Implementation notes"
  rhythm — applied to the v1 TOML schema sections.
- **`src/Terminal.Pty/ShellRegistry.fs`** and
  **`src/Terminal.Core/HotkeyRegistry.fs`** (PR-B + PR-O) — the
  canonical extensibility pattern for F# types in this repo. The
  OutputEvent / Channel / Profile / Intent types follow the same
  shape (DU + record + builtIns + lookup).
- **`src/Terminal.Core/Coalescer.fs`** Stream-profile-defaults
  docstring (PR-N) — already establishes the "one Stream profile
  is what the current Coalescer is" framing. The spec ratifies
  what PR-N anticipated.

### E.4 Verification

After this spec PR lands:

1. CI runs `dotnet test` on Windows-latest; no-op (no code
   changes).
2. CI Markdown link check passes — internal links (`tech-plan.md`
   §8 / §9 / §10 anchors, `MAY-4.md`, `STAGE-7-ISSUES.md`
   framework-taxonomy entries, `USER-SETTINGS.md` candidate
   settings catalog) all resolve.
3. CI Workflow lint passes.
4. Visual review: spec renders cleanly on GitHub. The Decisions
   Committed section reads as a sequence of position + rationale
   + tradeoff blocks, not an enumeration of options.
5. Cross-reference scan: `SESSION-HANDOFF.md` /
   `PROJECT-PLAN-2026-05.md` / `STAGE-7-ISSUES.md` /
   `DOC-MAP.md` / `CHANGELOG.md` all point at the new spec
   consistently.
6. The 11 sub-stages (8a-8f + 9a-9d + the §10 reframe) are
   described in enough detail that a future Claude Code session
   can pick any one of them, read it, understand what it ships
   and how to NVDA-validate it, and execute without further
   design conversation.

The substrate spec lands as a single doc-only PR. The actual
implementation of the framework is multi-stage future work,
the same way Stage 7 spanned 11 sequenced PRs. The maintainer
reviews the spec, approves what's committed (or asks for
revisions), and then the next session picks up sub-stage 8a as
the first concrete implementation work.

### E.5 Open questions reflected back to the maintainer

These are decisions this spec defers but documents as open. They
do not block the spec from landing now; they are addressed at
the relevant sub-stage when the maintainer has bandwidth.

1. **Filename for this spec file.** Picked
   `spec/event-and-output-framework.md` because it names both
   Concerns and signals "spec, not research." Alternatives if
   the maintainer prefers: `spec/framework.md`,
   `spec/extensibility.md`, or split into two files.
2. **Whether the spec should be one doc or split.** Picked one
   doc since Concerns 1 and 2 are deeply connected (Concern 1's
   dispatcher is Concern 2's emission substrate). Splitting if
   the spec grows unwieldy is a Phase 2 reorg.
3. **The kill-switch hotkey.** Picked `Ctrl+Shift+K` (mnemonic:
   K for Kill). No current claimant. Maintainer approval at
   first NVDA validation row (Stage 9d).
4. **Whether the `AppCommand` → `Intent` rename happens in this
   spec or is deferred to Stage 9b.** Picked deferred;
   `AppCommand` stays the type name in code until Stage 9b
   lands. The spec uses "Intent" as the conceptual term and
   acknowledges the code-side rename happens at 9b.
5. **TOML schema exact field names.** The v1 TOML examples in
   the spec are illustrative, not the final pinned schema.
   Pinning happens at Stage 9c implementation time when the
   maintainer can react to a real reference document. The spec
   documents the principles the schema follows
   (mirror-the-abstractions; one section per profile / channel /
   shell).
6. **The kill-switch precise scope.** The spec commits to
   "user-defined extensions disabled, built-ins still work." The
   exact boundary (does the kill switch disable a TOML-overridden
   binding for a built-in intent? does it disable a profile
   parameter override?) is pinned at Stage 9d implementation.
7. **NAudio palette specifics under the new EarconProfile.** The
   §9.3 palette (frequency-mapped tones for colour categories)
   is the v1 default. Whether the palette is itself
   user-customisable via TOML in v1, or that's deferred to
   Phase 2, is open.

These are documented here so future sessions can see what's
deferred vs. decided. The spec ships without their resolution;
their resolution happens at the relevant sub-stage.

## Closing

This spec converts MAY-4.md's research-phase enumeration into
v1 commitments. The substrate it defines — RawInput envelope +
Intent layer + Dispatcher on the input side; OutputEvent +
Profile + Channel on the output side — answers the maintainer's
framing for the cycle: "the core foundation of making the
functional backend into something that is extensible and usable
for as many different input and output mechanisms as possible,
including customizable event triggers, and the ability to
subscribe to an event feed, which is presently only being
served to NVDA, but needs to be abstracted so other output
mechanisms can choose what to do with it."

The maintainer reviews. The next session picks up Stage 8a.
