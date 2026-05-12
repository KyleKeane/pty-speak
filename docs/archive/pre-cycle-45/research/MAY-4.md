# pty-speak: Background Research for the Next Phases of Development

*A reference document for use alongside `spec/overview.md`, `spec/tech-plan.md`, `docs/PROJECT-PLAN-2026-05.md`, and `docs/SESSION-HANDOFF.md`. Not prescriptive. The decisions are Claude Code’s to make in conversation with the maintainer; this document gathers prior art, articulates tradeoffs, and surfaces questions worth resolving before committing to an approach.*

-----

## Why this document exists

After Stage 7 (the validation gate), the May-2026 plan sequences an Output framework cycle and an Input framework cycle. Three concerns are in scope across those cycles, and each has enough design surface area that approaching it without prior art will either reinvent existing solutions or miss subtle accessibility implications that already have known answers in adjacent systems.

The three concerns:

1. **Universal event routing** — whether and how to route every event in pty-speak through one named dispatch path with pre- and post-stages that built-in code and user code can register against through the same API. The same dispatcher’s contract also determines whether alternate input sources (assistive HID devices, OSC, MIDI, serial) can be added later without disturbing existing handlers; the awareness is flagged inside this concern even though implementation is not in immediate scope.
1. **Output framework** — whether and how to evolve the existing Coalescer-to-UIA-to-NVDA pipeline into a typed semantic stream with switchable verbosity profiles, addressing the Stage 5 verbose-readback issue in the process. The output side is also where modern presentation channels — spatial-audio rendering with ambisonics or HRTF, multi-line refreshable braille displays, and devices that do not yet exist — get absorbed without disturbing emission code, which makes the metadata schema the load-bearing piece of forward compatibility.
1. **Navigable streaming response queue** — whether and how to give a screen-reader user a way to orient inside output that arrives over time, particularly Claude Code’s streamed responses.

A short additional section follows the three concerns, drawing on a 2014 Diagram Center recommended-practices document the maintainer authored. It describes properties that good announcements should have (linguistic design) and a lifecycle model for interaction with dynamic UI elements (Discovery / Navigation / Selection / On-demand). These are not a fourth concern; they are an evaluation rubric the design proposal can apply to decisions in all three concerns.

Each section below presents what is known, what existing systems have tried, and what tradeoffs are visible. Questions appear inline where context makes them sharpest, and a consolidated list appears at the end.

A constraint on prose generated in subsequent work: code comments, NVDA announcements, error strings, log lines, and documentation use literal language. Words like “blind,” “blindly,” “see,” “look at,” “watch for” used as metaphors for unrelated cognitive states are replaced with their concrete referents — *uninformed*, *speculative*, *without diagnostic data*, *check*, *poll*, *subscribe to*. This is the same standard the README articulates and applies to anything produced downstream of this document, including the document itself.

-----

## Concern 1 — Universal event routing

### What the question is

pty-speak already has a number of distinct event-handling paths: the Stage 6 keyboard input layer, the Williams VT500 parser, the screen model, the Stage 5 Coalescer, the diagnostic-toggle hotkeys (Ctrl+Shift+G/B/H), the shell-switching hotkeys (Ctrl+Shift+1/2/3) with the registry extensibility model anticipated in §7.5, the Velopack auto-update path, and the Stage 11 announcement surface. Each is plumbed independently.

A reasonable architectural question is whether — at some point in the post-Stage-7 cycles — these paths benefit from being routed through one uniform dispatch substrate. The argument *for* uniformity is that current customization (a new diagnostic, a new shell, a new hotkey) requires custom plumbing each time, and that cost grows nonlinearly. The argument *against* is that uniform substrates often add latency on the keystroke path and can obscure the specific failure modes that the current explicit paths make legible.

### Prior art worth understanding

**Emacs and Emacspeak.** Emacs’s `defadvice` mechanism makes every named function an extension point: any other code can register *before*, *around*, or *after* advice on it. Emacspeak’s audio-formatting layer is built almost entirely out of advice on existing Emacs functions, which is what allows it to work with arbitrary third-party packages without those packages cooperating. The lesson is that *if every interesting transition in the system can be hooked, accessibility extension becomes layered customization rather than per-feature integration work*. The cost is that Emacs’s startup time is partly a function of how much advice is loaded, and Emacspeak users do feel it.

**NVDA add-on architecture.** NVDA’s add-ons subscribe to events the screen reader emits (gainFocus, valueChange, nameChange, app-lifecycle), can override per-app behavior, and can be activated or deactivated by configuration profile. Profiles are tied to applications, contexts, and triggers. Worth studying because it represents a working accessibility-focused plugin model with real users and real misbehaving plugins; the lessons about isolation and recovery are particularly relevant.

**JAWS scripting.** Long-running precedent for user-customizable per-app behavior. Its failure modes are the cautionary half of the story: scripts written for one app version break on the next, the scripting language is its own dialect that users must learn, and “what is this script doing?” is hard to answer at runtime.

**The shell-registry pattern already in pty-speak (§7.5).** The closest existing analog inside the codebase to “user-defined behavior plugged in through a registration API.” It already handles the lifecycle (teardown, respawn), the announcement contract (the “Switching to…” → “Switched to…” pair), and the failure mode (“Cannot switch to Claude Code: not found on PATH”). Whatever uniform substrate emerges, the shell registry is probably the most informative existing pattern to generalize from.

### Tradeoffs visible from here

A canonical event type plus a registry of handler stages is the textbook design. It admits pre/post stages, allows handlers to Continue, Replace, Cancel, or Branch, and gives every handler a read-mostly runtime context to look at app state. Costs include: the dispatcher becomes a hot path that must be measured carefully; the canonical event type becomes a coupling point that all event-emitting code must update when adding a new variant; the indirection makes stack traces longer and failures slightly harder to localize.

Three plausible registration paths for user-defined behavior, each with its own profile of cost and capability:

- A *declarative configuration path* (Tomlyn, since it’s already a planned dependency) where users say “on event X matching predicate P, perform built-in action A with parameters Q.” Most accessible to non-F# users. Easiest to validate at load time. Easiest to disable as a kill switch. Limited expressiveness — bounded by the repertoire of built-in actions.
- An *F# Interactive script path* where users drop `.fsx` files into a known directory and pty-speak loads them via `FsiEvaluationSession`. Closest analog to the Emacspeak experience: customization language is the application’s language. Real concerns include assembly-load isolation, exception containment in a screen-reader-critical app, and FSI startup cost. A subprocess sandbox (scripts run in a separate process talking to pty-speak via JSON-line) is slower but eliminates whole categories of failure.
- A *compiled extension assembly path* with a formal plugin contract. Most type-safe. Least dynamic. Probably the path that gets defined last, after the other two reveal what the contract should actually be.

These are not exclusive — a system could implement them in any order, or any subset.

### Input sources beyond the keyboard — awareness for future-proofing

*Not in scope for the current sprints. Flagged here so the architectural decisions made now do not foreclose this direction later.*

The current input surface is keyboard plus app-reserved hotkeys, with paste and focus reporting added in Stage 6. There are two categories of input source that may matter later, and naming them now lets the dispatcher’s contract be designed without accidentally encoding “input is keyboard” as a structural assumption.

**Category A — alternate access devices already in widespread accessibility use.** Switch interfaces, sip-and-puff devices, foot pedals, eye-gaze controllers, custom HID devices from accessibility hardware vendors (AbleNet, Tecla, Pretorian Technologies). Most of these expose themselves to Windows as standard HID devices that emit keyboard scancodes, joystick reports, or custom HID reports. Some users already have these devices configured to type into other apps; for pty-speak the question is whether the device is presenting itself as a keyboard (in which case it lands on the existing input surface for free) or as a non-keyboard HID device whose reports the app would need to consume directly.

**Category B — programmable input from creative-coding and maker hardware.** OSC messages from MIDI controllers running TouchOSC or similar, MIDI itself, serial messages from Arduino-class microcontrollers. The use case is a power user assembling a custom physical interface — a row of foot switches, a labeled hardware button box, a knob for scrubbing through the streaming response queue — and binding those inputs to internal pty-speak actions. This is conceptually adjacent to category A: a user with motor differences may build their own custom interface using exactly the same hardware that a creative coder uses for music. The two categories are technically distinct but the user populations overlap, and the architectural surface that supports one supports the other.

**The architectural principle worth establishing now: an intent layer.** The dispatcher’s input contract should treat *input source* as metadata on an event rather than as an event’s identity. A keystroke event, an HID-report event, an OSC-message event, and a serial-line event are all *input events* differing only in `source` and `payload`. The intent-mapping layer — separate from the dispatcher itself — translates them into named internal actions (“submit current input,” “switch to PowerShell,” “jump to last response,” “toggle debug logging”). The same named action can be triggered by a keystroke, a foot pedal HID report, an OSC `/pty-speak/submit` message, or a serial line reading `submit\n`. Built-in actions and user-defined actions register through the same name space.

This is the same pattern the Diagram Center framework calls *separating intended action from any required physical performance of action* — the IndieUI principle articulated in its Report 2. It is also the pattern that makes the existing app-reserved hotkey contract (Ctrl+Shift+1 = “switch to cmd,” etc.) extend naturally: the named action exists, and the hotkey is one binding among several possible bindings.

**What this implies for current architectural decisions, and what it does not.**

Implies:

- The canonical Event type from Concern 1 should be designed so that a `KeyPress` event variant is one of several `Input` variants that all share an intent-mapping path, rather than `KeyPress` being structurally privileged. Concretely: a `RawInput` outer envelope with a `source` field (`Keyboard`, `Hid`, `Osc`, `Serial`, `Network`, `Internal`) and a `payload` discriminated by source, mapped through an intent layer to a named action. The keyboard path stays exactly as it is in current code; the structure just leaves room for siblings.
- The handler registration API from Concern 1 should permit a handler to register against a *named action* as well as against a raw event, so that a user-defined behavior can be triggered by any future input source bound to that action.
- The Tomlyn declarative configuration path from Concern 1 should reserve a `[bindings]` or `[actions]` namespace for the future input-mapping configuration, even if v1 only populates it from keyboard bindings.

Does not imply:

- Implementing HID enumeration, OSC reception, or serial protocols now. Those are separate device-integration projects each with their own depth (Windows Raw Input API for HID, a UDP listener and OSC parser for OSC, `System.IO.Ports` plus framing logic for serial). They are the subject of a separate research report when the time comes.
- Committing to whether OSC, MIDI, or serial flows are bidirectional. The output framework in Concern 2 already admits a network fan-out channel, which is the natural place a return path would live; the input side and the output side meeting at a particular device is a per-device decision, not a global one. Deferred until the relevant input-device research happens.
- Picking a transport for OSC (UDP vs. TCP vs. WebSocket vs. OSC-over-MIDI). All flow through the same intent-mapping layer regardless; the transport is a per-source detail in the device-integration report.

**What the awareness costs versus what locking it out would cost.** The cost of building this awareness in now is small — it is a structural choice in the Event type and a reserved namespace in the configuration schema. The cost of *not* building it in is much larger: if the dispatcher is designed around `KeyPress` as a structural primitive, retrofitting alternate input sources later is a refactor that touches every handler. The cheapest moment to leave the door open is the moment the dispatcher is designed.

### Questions worth thinking about for this concern

- Is there current pain that a uniform dispatcher would relieve, or is it speculative architecture? A list of recently-painful integrations (the Ctrl+Shift+H liveness-probe addition? the shell-switch slot reshuffle?) would be the strongest evidence either way.
- Is the right scope “every event” or “events of certain categories”? Some events (parser internal transitions, low-level ConPTY reads) may belong below the dispatch layer for performance reasons. Where does the line sit?
- The runtime context object can be cheap or expensive. If it includes the full `History` and `Output` ring at every dispatch, it becomes the performance question. If it lazily computes those, the laziness becomes the correctness question. Which side gets prioritized?
- How are user handlers and built-in handlers ordered relative to each other? Strictly user-after-builtin? Configurable per event? Topological by declared dependencies? Each has implications for what users can and can’t override.
- Is hot-reload of user scripts a v1 requirement or a later addition? Hot-reload is high-value for a customization-heavy tool but adds substantial implementation cost (file watcher, partial state reconciliation, what to do with in-flight events when a handler is swapped).
- For the kill switch: hotkey, config flag, and CLI flag are three places it could live. Is one of them the canonical entry point and the others convenience aliases, or are all three first-class? Is the kill switch persistent across sessions until explicitly reversed, or one-session-only?
- For the input-sources awareness: is the right v1 footprint a `RawInput` outer envelope with a `source` field even though only `Keyboard` is populated, or is even that too speculative for the immediate sprint? The structural cost is low but it is still a design choice that the proposal should make deliberately rather than by default.
- For the intent layer: should named actions exist as a first-class registry from v1 (so that the existing app-reserved hotkeys are bindings to named actions rather than handlers in their own right), or should the intent layer arrive only when the second input source actually arrives?

-----

## Concern 2 — Output framework

*This concern is the substance of the post-Stage-7 Output framework cycle. The Stage 5 verbose-readback issue is the first concrete problem it would address; Stages 8 and 9 are downstream beneficiaries.*

### What the question is

The Coalescer-to-UIA-to-NVDA pipeline that Stage 5 made functional currently emits text-with-formatting toward UIA. The verbose-readback issue suggests that the right level of abstraction for the user-customization layer may be higher than text-plus-formatting — specifically, that emitting *what the output is semantically* (a prompt, a result, a spinner, a selection-list item) and letting a downstream layer decide *how to convey it* may give the user the right place to intervene without modifying the parser or the screen model.

The architectural question is whether to introduce that semantic layer now, defer it, or solve the verbose-readback issue at a lower layer first and revisit. The answer depends partly on whether the verbose-readback issue is one bug or a category of bugs.

### Prior art worth understanding

**W3C Aural CSS (CSS 2 Appendix A).** The original specification for separating “what” from “how” in spoken output. Defines `voice-family`, `pitch`, `pitch-range`, `stress`, `richness`, `speech-rate`, `cue-before`, `cue-after`, `pause-before`, `pause-after`, `azimuth`, `elevation`. The vocabulary is exactly the right level of abstraction for an accessibility application’s emission side, and it is engine-independent — the same emission drives Dectalk, ViaVoice, eSpeak, or Piper because the abstraction is at the right layer.

**Emacspeak’s personality model.** T. V. Raman’s implementation of Aural CSS in Emacspeak. The “personality” property attached to text is an Aural CSS setting; engine-specific modules (`dectalk-voices.el`, `outloud-voices.el`) map ACSS dimensions to engine-specific control codes. This separation has held up across thirty years of changing speech engines. The architecturally interesting move is that *personality is treated as a list, not a single value*: a piece of text can have `(voice-bolden voice-monotone)` applied cumulatively, which is a clean way to express compositional emphasis. Worth reading the `voice-setup`, `dtk-speak`, and `emacspeak-personality` modules directly.

**ARIA live regions.** `aria-live="polite"` versus `aria-live="assertive"` versus `aria-live="off"` is the Web’s mature answer to the turn-taking problem in screen-reader output. Polite waits for the screen reader to finish current speech; assertive interrupts. The model is well-understood by NVDA, JAWS, and Narrator users, which means using the same priority taxonomy in pty-speak gives users transferable intuition.

**Windows Terminal’s UIA implementation.** The reference for what the underlying surface can express, since pty-speak targets the same surface. `UiaRaiseNotificationEvent` has its own kind taxonomy (`NotificationKind_ItemAdded`, `NotificationKind_ItemRemoved`, `NotificationKind_ActionCompleted`, `NotificationKind_ActionAborted`, `NotificationKind_Other`) and processing taxonomy (`NotificationProcessing_ImportantAll`, `_ImportantMostRecent`, `_All`, `_MostRecent`, `_CurrentThenMostRecent`). These map onto ARIA priorities in non-obvious ways and the mapping is worth pinning down explicitly.

**The README’s three named failure modes.** Spinner storms, selection lists read flat, ANSI styling invisible. Each is a candidate test case for whether the framework would address the issue at the right architectural layer. If the framework’s default profile naturally produces correct behavior for all three, that is strong evidence the abstraction is at the right level. If solving them still requires reaching below the framework, that is evidence to revisit the design.

### Tradeoffs visible from here

A typed semantic output stream with switchable verbosity profiles has clear benefits: the spinner-storm fix becomes a default-profile entry that the user can override (some users genuinely want spinner feedback for long-running test suites); the selection-list semantics surface gets a clean home; the SGR-to-Personality mapping happens in one named place rather than scattered across the parser and the renderer.

The costs are real:

- Another type to maintain alongside the canonical Event type from Concern 1, with similar coupling implications.
- A profile schema is a new user-facing surface that must be designed, documented, and version-managed across releases.
- Threading and ordering invariants become per-channel rather than global, and getting them wrong can produce subtle interleaving bugs that are hard to reproduce.
- The mapping from semantic priority to the underlying UIA `NotificationProcessing` kind is non-obvious and the wrong mapping degrades behavior on JAWS or Narrator without anyone noticing until a user reports it.

### Channels that the framework might support

A reasonable set to design against, presented as design surface rather than as commitment:

- *NVDA via UI Automation* — the current path. Render actions: raise `TextChanged` on a Document range, raise `UiaRaiseNotificationEvent` with given kind and processing, populate `UIA_*AttributeId` per the personality.
- *JAWS* — same UIA surface. Behavior diverges from NVDA in known places; whether that requires a separate channel or is handled by per-screen-reader profile entries on a single UIA channel is itself a design question.
- *Narrator* — same UIA surface; verified separately because Narrator’s UIA consumption diverges from NVDA’s at specific points.
- *Embedded self-voicing TTS* — Piper TTS as a subprocess, since it is already named in the README’s deferred-dependencies list. Useful for users who prefer no screen reader, for SSH from a sighted-user machine into an accessible session, and as a fallback when a screen reader is misbehaving.
- *WASAPI earcons* — the Stage 9 channel. Plays a non-speech cue on a separate audio session that does not duck NVDA. Worth treating as a distinct channel because it has different concurrency, ordering, and priority rules from speech.
- *Spatial-audio engine* — a custom audio-rendering channel that places sounds at perceptual locations around the user’s head using ambisonics or HRTF rendering. The presentation surface for a screen-reader user gets dramatically larger when notification *type*, notification *source*, and notification *priority* are mappable to *location*, *voice*, and *timbre* simultaneously. Different categories of events can come from different perceived directions, different categories of voices can speak from different positions, and sonification of structured data (a numeric series, a tool-call progress, a streaming response’s relative position in the queue) can use the spatial dimension as a structural axis. Likely realized by a separate audio-engine subprocess (an existing engine like SuperCollider, which the README already names; or a custom engine via OpenAL Soft or Steam Audio for HRTF) talking to pty-speak via the same JSON-line protocol the Piper subprocess would use. Concurrency, ordering, and priority rules differ further still from earcons because spatial events carry an additional `position` field that must be respected even when the channel is preempting itself.
- *Refreshable braille* — including multi-line tactile graphics displays such as the Monarch and the Dot Pad. A single braille channel is insufficient for these devices because they have *spatial regions* the way a screen has visual regions: different categories of notifications can render in different areas of the display at the same time, and a status line, a current-segment readout, and a streaming-tail preview can coexist non-overlappingly. Standard single-line braille displays (typical BRLTTY targets) become a degenerate case of the same channel where the device exposes one region. The channel needs to know the device’s capabilities — region count, region geometry, whether it supports tactile graphics — and route notifications according to per-profile mappings of `Semantic` to `region`. The contract here is necessarily forward-looking; new devices in this category appear regularly, and the channel abstraction should be able to absorb them without changes to the emission side.
- *FileLogger* — already exists; promoting it to a first-class channel means log content is governed by the same profile machinery as speech, which makes the Ctrl+Shift+; clipboard-copy flow naturally capture everything the user heard.
- *Network fan-out* — TCP or named-pipe channel for piping events into another tool (a second screen reader on a different machine, an LLM-based summarizer, a custom transcription service). Probably defer implementation; design the abstraction to admit it.
- *Plain stdout / stderr* — for CI, for SSH sessions without a screen reader, for `pty-speak --no-speech` modes.

The principle threading through this list is that *the emission side stays stable while the rendering side absorbs the future*. The spatial-audio channel and the multi-region braille channel are particularly good stress tests for the OutputEvent metadata schema, because they require the schema to carry enough structure (semantic category, priority, optional spatial hint, optional region hint, source identity for voice/timbre selection) that a channel implementation written years from now for a device that does not exist today can still make sensible routing decisions from existing emissions. If the schema is right, adding the Dot Pad in five years is a channel implementation and a profile entry. If the schema is wrong, every new device requires changes through the parser, the screen model, and the emission sites — which is the cost the framework is meant to avoid.

### Threading and priority taxonomy

The taxonomy that emerges naturally from ARIA plus the UIA processing kinds is something like:

- *Interrupt* — flush channel queue and interrupt current speech. Maps to UIA `NotificationProcessing_ImportantMostRecent` or similar.
- *Assertive* — preempt pending Polite events but do not interrupt currently-speaking content. Maps to `NotificationProcessing_All` with high importance.
- *Polite* — queue and render in order. Maps to `NotificationProcessing_All` with normal importance.
- *Background* — render only when channel idle for a configurable interval. Useful for status that should be available on demand but should not interrupt anything.

Per-channel order preservation by default (within NVDA, events render in emission order); cross-channel parallelism allowed unless a profile entry constrains it. The threading primitive presumably reuses whatever the existing codebase has standardized on (`MailboxProcessor`, `System.Threading.Channels`, TPL Dataflow); introducing a second primitive for the output framework would be a non-goal.

### Questions worth thinking about for this concern

- Is the verbose-readback issue from Stage 5 one bug or a category? If one bug, the framework may be over-investment for the immediate problem and a targeted fix is the right move. If a category, the framework pays for itself the first time it produces a default-profile entry that the user can override per-shell.
- What does a verbosity profile entry look like in user-facing form? The TOML example sketches a possibility but the schema commits to a vocabulary (`semantic`, `priority`, `channels`, `template`, `uia_role`). What is the actual minimal viable schema?
- For per-shell profiles (default vs. claude_code vs. powershell), how is the “current shell” signal sourced? The shell registry already knows the answer for shells launched through Ctrl+Shift+1/2/3, but a user could spawn an inner shell via the running shell (cmd inside PowerShell) — does the profile follow that or stay with the outer shell?
- How does the user discover what profile entries are active? An “explain why pty-speak said X” command — given a recent NVDA announcement, trace which `OutputEvent` it came from, which profile entry matched, which channel rendered it — would be high-value for debuggability but is itself a non-trivial feature.
- Is there value in profile composition (a base profile + an overlay profile + per-session overrides), or is one flat profile per session sufficient? Composition is more flexible and harder to reason about; the question is whether the flexibility is needed.
- The Piper TTS subprocess invocation is GPL-3 in the form named in the README. What are the implications for shipped binaries vs. users-bring-their-own-Piper? Worth resolving before the self-voicing channel is implemented.
- For the network fan-out channel: is there a scenario the maintainer can name where remote pair-programming or external semantic analysis is a real near-term use case, or is it speculative? If speculative, the abstraction is still cheap to admit, but if real, it shapes the channel API.

-----

## Concern 3 — Navigable streaming response queue

*This concern overlaps Stage 10 (review-mode toggle, Alt+Shift+R reserved) and a longer-term review-cursor model. The UX problem is genuinely underexplored, and the most useful framing may be research rather than implementation planning.*

### What the question is

When output arrives over time — Claude Code’s streamed responses, long shell commands, kernel computations that emit progress — a screen-reader user needs to be oriented inside a moving target. Sighted users get this orientation almost free from the visual layout: text appears, scrollbars move, attention is drawn by motion. None of that translates to a screen reader.

The question is whether pty-speak builds a small set of navigation primitives over a structured representation of streaming output, or treats the screen buffer as the navigation substrate and gives the user enhanced review-cursor commands over it. These are not mutually exclusive but they have very different implementation profiles.

### Prior art worth understanding

**Emacs `comint-mode`.** The closest existing precedent for navigating a live process buffer with a screen reader. Conventions like “previous prompt,” “next prompt,” “previous output block,” “next output block,” tied to the buffer’s structure. Worth reading directly because the keystroke conventions are well-honed by decades of use, and because the failure modes (long-running process whose output exceeds the navigation cache) are documented.

**NVDA’s review cursor.** The model NVDA users already know: a cursor independent of the system focus that can be moved by line, word, character, and used to read content without changing focus. The review cursor’s interaction with live regions is the relevant precedent — when does new content move the cursor automatically, when does the cursor stay put?

**ARIA `aria-live` semantics for incremental updates.** `aria-live="polite"` updates do not interrupt; `aria-live="assertive"` updates do. The model is widely understood by NVDA/JAWS/Narrator users.

**Alexa Skills voice interaction guidelines.** Conventions for “still thinking” vs. “done” announcements, for indicating that more content is coming, for handling barge-in. The precedent matters because Alexa is one of the few systems that treats *streaming arrival of structured output* as a first-class UX problem.

**Chat-app live regions.** Slack, Discord, IRC clients with screen-reader support have all tried something here. None has an answer that translates cleanly because the use cases differ — chat is human-paced, Claude Code is sub-second-paced — but the catalog of attempts is informative.

**The Diagram Center and AsTeR work on structured auditory presentation.** The principle that *structure*, not surface text, drives auditory presentation. AsTeR’s tree-walking model for technical documents is distant but conceptually relevant — it treats a document as a navigable structure and the spoken rendering as one view over that structure.

### Tradeoffs visible from here

A typed segment forest representing each response as a tree of typed segments (text, code, list, list-item, table, tool-call, tool-result, citation, error) gives navigation primitives clean targets to move between. The cost is that producing this representation requires parsing Claude Code’s stream format (and other shells’ output, each in their own way), and parser bugs become navigation bugs.

The alternative — enhancing the review cursor over the screen buffer — is cheaper and reuses NVDA users’ existing intuition. The cost is that the screen buffer doesn’t always know about semantic structure (a code block looks like text plus ANSI, a tool call looks like text), so the navigation primitives are limited to syntactic heuristics on the rendered surface.

A hybrid is plausible: the screen buffer remains the rendering substrate, an opt-in semantic layer parses known shell formats (Claude Code first, others later), and navigation primitives prefer the semantic layer when it is available and fall back to syntactic boundaries when it is not.

### Navigation primitives that have proven their value elsewhere

Worth considering, not adopting wholesale:

- *Jump to latest* — move cursor to the most recently arrived segment. The “I just heard a notification, take me to it” gesture.
- *Jump to last response* — move cursor to the start of the most recent top-level response. The “what did Claude just say?” gesture.
- *Step within current response* — next/previous segment, next/previous sentence, next/previous code block. Hotkeys, not menus.
- *Step across responses* — older response, newer response.
- *Up/down the segment tree* — into and out of nested structures (into a code block from the paragraph that introduced it, out to the parent response).
- *Read from cursor to end* — speak everything from the cursor’s current position forward, continuing as new content arrives if the response is still streaming.
- *Stop reading* — and remember position.
- *Status query* — “where am I, and is this segment still streaming?” One key, one-line answer.

### Notification policy when new content arrives below the focus

Three modes a user might switch between, configurable per profile:

1. *Quiet.* No interruption. A status query reveals new content; pty-speak does not announce it.
1. *Subtle cue.* A short non-speech earcon (Stage 9 channel) plays when a new segment finishes arriving. The earcon’s timbre varies by segment kind. No speech interruption.
1. *Verbal heads-up.* A brief polite-priority spoken phrase (“new section below,” “tool result available”). Configurable threshold so it does not fire for every chunk of a streaming paragraph.

ARIA’s polite/assertive distinction maps onto Concern 2’s priority taxonomy: subtle-cue is Polite on the earcon channel, verbal-heads-up is Polite or Assertive on the speech channel depending on configuration, quiet is suppressed at the profile layer. Default mode 2 (earcons) is one reasonable choice because earcons convey presence without interrupting reading; default mode 1 (quiet) is another reasonable choice on the principle that no announcement is the safest default for a power user.

### Questions worth thinking about for this concern

- Is the right v1 scope “navigable streaming output for Claude Code specifically” or “general streaming-output navigation that Claude Code happens to use”? The first is faster to ship and harder to extend; the second is slower and more reusable. Each has a defensible answer.
- Whose preferences are ground truth for the v1 navigation keyboard layout? The maintainer is the primary user, but the design has implications for future users. Is “what works for the maintainer” the right v1 anchor and “what generalizes” a v2 concern?
- For the segment representation: structured tree (with ParentId, Kind, Status, etc.) versus flat list-with-annotations? The tree is more expressive; the list is easier to keep in sync with arriving chunks. The right answer probably depends on how much nesting Claude Code’s stream actually produces in practice.
- For streaming responses that are still arriving: when the user has the cursor in segment N and segments N+1 and N+2 arrive, what happens? Does the cursor stay at N? Does it move to N+2? Does it depend on whether the user was actively reading? This is the hardest UX question and probably needs the maintainer’s preference as ground truth.
- Is there a “freeze” mode the user explicitly enters, where new content is buffered but not announced and the user can navigate the historical record without the moving target? If so, how is exit from freeze handled?
- Does the navigation layer need to interact with NVDA’s existing review cursor, or does it operate independently? If independently, the user has two cursors and must keep them straight; if integrated, pty-speak has to coordinate with NVDA’s internal model, which is harder.
- For known shell formats (Claude Code first), is the parser opt-in per shell or always-on with a fallback? And: who maintains the parser when Claude Code’s stream format changes?

-----

## A note on linguistic design and interaction with dynamic interfaces

*The 2014 Diagram Center recommended-practices document on Interactive Scientific Graphics (Wolfram Research, the maintainer as author) developed a framework for verbal description of dynamic content and digital control objects that maps cleanly onto pty-speak’s problem space. This section summarizes the parts most directly applicable.*

### Why this is relevant

The framework was built for describing dynamic scientific graphics and the digital control objects that manipulate them. It generalizes naturally because the underlying problem is the same one pty-speak solves: a screen-reader user needs to engage with an interface whose state changes over time, where what is being communicated is information rather than visual surface, and where the announcements have to be *useful* — accurate, equivalent to what a sighted user gets, and not noisy.

Two structures from the framework are particularly transferable: a vocabulary of properties that good announcements should have (linguistic design), and a lifecycle model for interaction with any dynamic UI element (Discovery → Navigation → Selection → On-demand). Both shape what pty-speak emits and how users engage with it. They are research, not prescription, but they are research the maintainer has already done and that the design proposal can profitably draw on.

### Linguistic design — properties of good announcements

The framework groups quality criteria for announcements into four categories. These apply to anything pty-speak speaks via NVDA, anything a verbosity profile renders to any channel, and anything a user-defined handler emits.

**Content properties:**

- *Accurate* — do not misrepresent. Verify that the announcement reflects the underlying state.
- *Equivalent* — describe all the information present. Do not silently summarize away information a sighted user would have.
- *Objective* — only describe information actually present. Do not include inferred conclusions or commentary.
- *Essential* — only represent necessary information. Skip decorative elements.

**Vocabulary properties:**

- *Contextual* — use words from the relevant domain. A Claude-Code tool-call announcement should use Claude-Code vocabulary; a PowerShell prompt announcement should use shell vocabulary.
- *Common* — use words a user can search for. Idiosyncratic terminology is a barrier.
- *Appropriate* — match the user’s expected knowledge level.
- *Consistent* — do not refer to the same thing by multiple names. (If the framework calls it a “selection list,” do not also call it a “menu” or “chooser.”)
- *Unambiguous* — do not use one word for multiple things.

**Phrasing properties:**

- *Clear* — information should be easy to extract.
- *Concise* — short phrases over full sentences. Audio Description Coalition’s guidance is explicit: “do not use complete sentences; stay with short phrases.”
- *Understandable* — first-pass comprehension should not require repetition.

**Delivery properties:**

- *Apt* — identify the changing features specifically. Don’t re-describe the whole interface when only one element changed.
- *Synchronous* — describe changes when they occur, not on a delay.
- *Controllable* — present from general to specific. The most important information first; precision available on request.

The verbosity-profile schema in Concern 2 is the natural enforcement point for several of these properties. *Consistent* and *Unambiguous* in particular are properties of the profile *across* its entries — if two profile entries use different words for the same `Semantic` tag, that is a profile defect detectable at load time.

### Interaction lifecycle for dynamic UI elements

The framework decomposes interaction with a dynamic UI element into four stages, each with its own communication contract. This applies to selection lists, sliders, toggles, the streaming response queue’s navigable cursor, any future review-mode widget, and any user-defined widget added through the handler chain.

**Discovery** — the user encounters the element for the first time, or returns to it after focus moved away.

- *Identity* — clear, appropriately-scoped title that identifies what the element is and what parameter it controls.
- The Apple iOS guidance is specifically applicable: *begin with the verb, omit the subject*. “Toggle sound on or off” rather than “this is a button that will toggle sound.”

**Navigation** — the user is moving among possible values without committing to one.

- *Common* — mimic well-known navigation conventions (arrow keys, modifiers for larger steps).
- *Current value, approximate* — give a real-time qualitative cue as values change. For a slider this might be “fourteen percent” or a tone proportional to value; for a selection list, the candidate’s name. Approximate is the right register here because *precision during navigation is noise* — the user is sweeping, not committing.

**Selection** — the user commits to a value.

- *Common* — mimic well-known selection conventions (spacebar or Enter).
- *Current value, precise* — the announcement after selection is the *full, precise* specification. “Eighty-six percent: 172: min 0: max 200.” This is where precision matters; the user is now operating against this value.

**On-demand** — at any point in the lifecycle, the user can ask for full information.

- *Operation* — how to use the element.
- *Overview* — what the element does in general.
- *Function* — the specific effects of using it.
- *Value* — the current precise value.

Two principles cut across all four stages and are worth lifting up explicitly:

*Qualitative in real time, precise on demand.* During navigation, give an approximate cue that conveys the right shape without flooding. When the user explicitly asks (a status query keystroke, a “describe focus” command), give the full precise picture. This maps directly onto the Concern 3 streaming-response navigation problem — sweeping through segments should not announce every segment in full; arriving at a segment of interest should give the user a way to ask for full content.

*Result-focused, not action-focused.* Announce what changed, not what was pressed. “Sound is off” rather than “checkbox toggled.” “Switched to PowerShell” rather than “Ctrl+Shift+2 received.” pty-speak already follows this in its shipped announcements; the framework formalizes it as a property the verbosity profile can be checked against.

### How this interacts with the three concerns

These properties are not a fourth concern. They are an evaluation rubric the design proposal can apply to any decision in the three concerns:

- *Concern 1 (event routing):* user-defined handlers should be reviewable against these properties — a handler that emits announcements is part of pty-speak’s voice and should match its voice.
- *Concern 2 (output framework):* the verbosity profile is the right enforcement point for several of these properties. The “explain why pty-speak said X” command sketched there can also be the way the maintainer evaluates announcement quality at runtime — given a recent announcement, was it accurate, equivalent, objective, essential? Was the phrasing concise?
- *Concern 3 (streaming navigation):* the Discovery / Navigation / Selection / On-demand lifecycle is directly applicable. Entering a streaming response is Discovery; sweeping segments is Navigation (qualitative cues, approximate); landing on a segment of interest is Selection (precise content, full context); the status query is On-demand.

### Questions worth thinking about for this section

- Should the `OutputEvent` schema carry a *verbosity register* (e.g., `Approximate` vs. `Precise`) so that the same content can be rendered at the right register for the user’s current interaction stage, or is that a profile-layer concern?
- Should the verbosity profile have a checker — a static-analysis pass at load time that flags inconsistent vocabulary across entries, ambiguous templates, or entries that violate the result-focused property?
- For the on-demand information requirement (Operation / Overview / Function / Value): is there a single keystroke that any focused widget responds to with full information, similar in spirit to Ctrl+Shift+H but scoped to “describe what is in focus”?
- The framework distinguishes information that should be *always* available, vs. *during discovery only*, vs. *on request*. Does pty-speak’s announcement model carry that distinction explicitly, or does it currently conflate them?

-----

## Cross-cutting considerations

These apply to any approach taken across the three concerns and are worth noting whether or not the concerns are addressed jointly or separately.

**Failure modes are accessibility issues.** A misbehaving handler, a profile load failure, a sink error (NVDA not running, Piper subprocess died, log file not writable) cannot crash pty-speak and cannot be silent. The user needs to know what failed and how to recover. The Ctrl+Shift+H pattern (one keystroke, one-line answer) is the right precedent for failure surfacing.

**Discoverability through the same channel as use.** Whatever extension and customization surfaces emerge, they should be enumerable at runtime by the user via the same NVDA-readable channel that everything else uses. “List all events,” “list current handlers on event X,” “describe profile entry for Y” — if these are hotkey-accessible they are discoverable; if they require reading source code they are not.

**Documentation as deliverable.** The `docs/UPDATE-FAILURES.md` precedent (enumerating literal NVDA announcements) is an unusually accessible way to document a system’s behavior. Whatever surfaces emerge from these concerns probably warrant the same treatment — `EVENTS.md`, `OUTPUT.md`, `STREAMING.md` or whatever the working names become — because users debugging an unexpected announcement can grep their way to its source.

**Performance budget on the keystroke path.** The user receives per-character audio feedback at typing speed. Anything more than a few milliseconds of overhead per keystroke is felt. Whatever is built measures and budgets this explicitly.

**Backward compatibility through the transition.** Existing pty-speak behavior should remain the default. Any framework introduction expresses current behavior as default configuration of the new framework, with no behavior change at the user-visible level until the framework is in place — and then, deliberate change with each step verified against `docs/ACCESSIBILITY-TESTING.md`.

**Forward compatibility of the OutputEvent schema.** New output devices appear regularly — the spatial-audio engines and multi-line braille displays in Concern 2’s channels list are current examples; the next generation will include things this document cannot name. The schema for OutputEvent and its metadata is the contract that determines whether absorbing those devices is a one-channel-implementation cost or a costly retrofit through the parser, screen model, and emission sites. This argues for being generous with metadata at emission time (carry semantic category, priority, source identity, optional spatial hints, optional region hints, optional structural-context references even when the current set of channels does not consume all of them) and for treating the schema as an explicitly versioned, explicitly extensible contract. The cleanly-abstracted OutputEvent is the load-bearing piece of forward compatibility; if the schema is right, the framework absorbs new devices without disturbing anything upstream of it.

**Alignment with existing convention.** `CLAUDE.md` and `docs/SESSION-HANDOFF.md` define how Claude Code operates in this repository. Whatever emerges from these concerns aligns with those conventions, extends rather than contradicts them, and updates them where the contract expands.

**The literal-language constraint.** Sight-based metaphors applied to non-sight phenomena get replaced with their concrete referents in all generated prose: code comments, NVDA announcements, error strings, log lines, documentation. The standard is that if the literal phrase is available and equally clear, the literal phrase is used. This is the standard the README articulates and it propagates here.

-----

## Inspirations and reference reading

Worth direct consultation, in roughly the order that pays off across the three concerns.

- **Emacspeak** — `voice-setup`, `dtk-speak`, `emacspeak-personality`, T. V. Raman’s writeups on audio formatting and the Aural CSS implementation. The personality abstraction is the cleanest precedent for Concern 2’s emission-side abstraction. `comint-mode` is the closest existing precedent for Concern 3’s streaming navigation. The `defadvice`-based extension model is the cleanest precedent for Concern 1.
- **W3C Aural CSS (CSS 2 Appendix A).** The vocabulary that drives Concern 2’s `Personality` if a personality concept is introduced. Engine-independent by design.
- **NVDA add-on architecture and configuration profiles.** Working precedent for per-application accessibility customization with real users.
- **NVDA Controller Client documentation and `UiaRaiseNotificationEvent` semantics.** The current pty-speak path; capabilities and limitations bound what Concern 2 can express today. The mapping from semantic priority to UIA `NotificationProcessing` kind is worth pinning down explicitly when the design solidifies.
- **JAWS scripting** as cautionary precedent. Long-running per-app customization with known failure modes around versioning, dialect cost, and runtime opacity.
- **AsTeR (Raman) and the Diagram Center work on interactive scientific graphics.** The principle that *structure*, not surface text, drives auditory presentation. Distant from pty-speak’s immediate problems but conceptually foundational. The Diagram Center work is also the precedent for the linguistic-design and dynamic-interface-interaction frame in the section below.
- **Sonification, ambisonics, and HRTF rendering.** *The Sonification Handbook* (Hermann, Hunt, Neuhoff) is the canonical reference for sonification techniques. SuperCollider (already named in the README’s optional dependencies) is a working precedent for treating audio rendering as a separate, scriptable engine. OpenAL Soft, Steam Audio, and Resonance Audio are open-source HRTF/ambisonic renderers that can be invoked as subprocess audio engines. The shared concept across these systems is *spatial sound as a perceptual organizing structure* — the same idea Aural CSS encodes in its `azimuth` and `elevation` properties, but realized with modern HRTF processing rather than two-channel pan.
- **Refreshable multi-line braille displays.** The Monarch (APH, multi-line braille and tactile graphics) and the Dot Pad (Dot Inc., multi-line tactile graphics) are the current generation. BRLTTY remains the reference for single-line braille; whatever multi-line abstraction emerges should keep BRLTTY as a degenerate case rather than treating multi-line as the special case.
- **ARIA live regions** (`polite`, `assertive`, `off`) for the priority/turn-taking model in Concern 2 and the notification-mode model in Concern 3.
- **Alexa Skills voice interaction guidelines** for turn-taking conventions in Concern 3.
- **Windows Terminal’s UIA implementation** as the reference for what the underlying surface can express.

-----

## Consolidated questions

These collect the questions surfaced inline above plus a few that span more than one concern. The intent is to give the maintainer a single list to react to before any architectural commitment is made.

### Questions on Concern 1 (universal event routing)

1. Is there current pain that a uniform dispatcher would relieve, or is it speculative architecture? What recent integrations have been hardest, and what would have made them easier?
1. Is the right scope “every event” or “events of certain categories”? Where does the line sit — for example, do parser-internal transitions and low-level ConPTY reads stay below the dispatch layer for performance reasons?
1. The runtime context object can be eager (full state at every dispatch) or lazy (computed on demand). Which side gets prioritized — performance or simplicity of handler authoring?
1. How are user handlers and built-in handlers ordered relative to each other? Strictly user-after-builtin, configurable per event, or topological by declared dependencies?
1. Is hot-reload of user scripts a v1 requirement or a later addition?
1. For the kill switch — hotkey, config flag, CLI flag — is one of them canonical and the others convenience aliases, or are all three first-class? Does the kill switch persist across sessions or only within one?
1. Of the three registration paths (declarative TOML / F# Interactive / compiled assemblies), which is implemented first, and is in-process FSI acceptable or is the subprocess sandbox the right starting point?
1. For the input-sources awareness: is the right v1 footprint a `RawInput` outer envelope with a `source` field even though only `Keyboard` is populated, or is even that too speculative for the immediate sprint? The structural cost is low but it is still a design choice the proposal should make deliberately.
1. For the intent layer: should named actions exist as a first-class registry from v1 (so existing app-reserved hotkeys are bindings to named actions rather than handlers in their own right), or should the intent layer arrive only when the second input source actually arrives?

### Questions on Concern 2 (output framework)

1. Is the verbose-readback issue from Stage 5 one bug or a category? If one bug, is a targeted fix preferable to framework introduction?
1. What is the actual minimal viable schema for a verbosity profile entry?
1. For per-shell profiles, how is the “current shell” signal sourced when an inner shell is spawned by a running shell?
1. How does the user discover what profile entries are active — is there value in an “explain why pty-speak said X” command that traces from a recent announcement back to the profile entry that produced it?
1. Is there value in profile composition (base + overlay + session overrides), or is one flat profile per session sufficient?
1. The Piper TTS subprocess invocation is GPL-3 — what are the implications for shipped binaries vs. users-bring-their-own-Piper?
1. For the network fan-out channel — is there a real near-term use case the maintainer can name, or is it speculative? Speculative still admits the abstraction cheaply, but a real use case shapes the channel API.
1. The mapping from semantic priority (Interrupt / Assertive / Polite / Background) to UIA `NotificationProcessing` kind is non-obvious. Does the design proposal pin this down explicitly per priority and per screen reader, or defer it?
1. For the spatial-audio channel: is the right initial bet a SuperCollider subprocess (already in the README’s optional dependencies, GPL-3), an OpenAL Soft / Steam Audio HRTF renderer in-process or via subprocess, or a deliberately-deferred contract that admits any of these later? What is the minimum metadata an `OutputEvent` must carry to make spatial routing decisions later, even if no spatial channel is implemented in v1?
1. For multi-line braille displays (Monarch, Dot Pad): is a region model (named regions on the device, profile entries map `Semantic` to region) the right abstraction, or is a tactile-graphics model (the channel renders structured content as raised-line graphics) closer to the device’s actual capability? Probably both; the question is which is v1 and which is v2.
1. The OutputEvent metadata schema is the load-bearing piece of forward compatibility. Is it explicitly versioned? Are unknown fields preserved on round-trip so that a profile entry written for a future device does not get truncated by an older pty-speak that does not yet know about the device? The design proposal commits to a schema-evolution policy as part of the output-framework definition.

### Questions on Concern 3 (navigable streaming response queue)

1. Is the right v1 scope “navigable streaming output for Claude Code specifically” or “general streaming-output navigation that Claude Code happens to use”?
1. Whose preferences are ground truth for the v1 navigation keyboard layout — is “what works for the maintainer” the right v1 anchor and “what generalizes” a v2 concern?
1. Structured tree representation versus flat list-with-annotations for segments — which trades better against actual Claude Code stream behavior?
1. When the user has the cursor in segment N and segments N+1, N+2 arrive — what happens? Does the cursor stay, move, or depend on whether the user was actively reading?
1. Is there a “freeze” mode for navigating the historical record while new content is buffered? How is exit from freeze handled?
1. Does the navigation layer interact with NVDA’s existing review cursor, or operate independently? Two cursors versus integrated coordination — each has a defensible answer.
1. For known shell formats: parser opt-in per shell or always-on with fallback? Who maintains the parser when Claude Code’s stream format changes?

### Questions on linguistic design and dynamic-interface interaction

1. Should the OutputEvent schema carry a *verbosity register* (e.g., `Approximate` vs. `Precise`) so that the same content can be rendered at the right register for the user’s current interaction stage, or is this a profile-layer concern?
1. Should the verbosity profile have a checker that runs at load time and flags inconsistent vocabulary across entries, ambiguous templates, or entries that violate the result-focused property?
1. For the on-demand information requirement (Operation / Overview / Function / Value): is there a single keystroke that any focused widget responds to with full information, similar in spirit to Ctrl+Shift+H but scoped to “describe what is in focus”?
1. Does the announcement model carry the always-available / discovery-only / on-request distinction explicitly, or does it currently conflate them?
1. The Discovery / Navigation / Selection / On-demand lifecycle is articulated for digital control objects in the 2014 framework. Should pty-speak’s selection-list semantics, slider-equivalent widgets, and streaming-response navigation conform to this lifecycle as a quality bar, even if they are not framed as control objects in the codebase?

### Cross-cutting questions

1. Are these concerns implemented in sequence or in parallel? They are connected (Concern 1’s dispatcher is the substrate Concerns 2 and 3 emit through; Concern 2’s earcon channel is the substrate Concern 3’s notification modes use), but the connections may not require strict sequencing.
1. What’s the right scope for the first deliverable? “Architecture proposal for all three” is one reasonable scope; “architecture proposal for Concern 2 with the others sketched in less detail” is another, given that Concern 2 is the one with the nearest-term motivating problem.
1. Is the maintainer-preference interview for Concern 3 a separate document, an inline section of the architecture proposal, or a working-session conversation that produces notes? Each shapes how the answers get captured and revisited.
1. Is there appetite for a small spike on each concern before the formal proposal — a half-day sketch in code that probes the actual costs — or does the maintainer prefer the proposal to come from analysis alone?
1. The input-device research is deferred but acknowledged. Does it want to live as a future companion document to this one, or as a section that gets folded back in once the immediate output and event-routing work has settled? Either is defensible; flagging it now means the deferral is deliberate rather than oversight.

-----

*This document is a starting point for the conversation, not a conclusion. The decisions are Claude Code’s to make in dialogue with the maintainer, informed by what the actual codebase and the actual user experience reveal once the proposal phase begins.*