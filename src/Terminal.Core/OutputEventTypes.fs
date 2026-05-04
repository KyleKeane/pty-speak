namespace Terminal.Core

open System.Collections.Generic

/// Stage 8a — output framework substrate types.
///
/// This module defines the v1 OutputEvent + Channel + Profile
/// abstractions that the framework cycles roll out across
/// sub-stages 8a–8f (output side) and 9a–9d (input side). The
/// canonical reference for the shape of these types and the
/// rationale for each field lives in
/// `spec/event-and-output-framework.md` Part B (output framework).
///
/// Stage 8a installs the substrate behaviour-identically: the
/// existing `ScreenNotification → Coalescer → drain → Announce`
/// pipeline is rerouted so the drain produces an `OutputEvent`
/// and dispatches through the framework, but the Coalescer (and
/// its PR-M / PR-N internals) stays unchanged. Subsequent stages
/// fill in the substrate:
///
/// - **8b** absorbs the Coalescer into the Stream profile;
///   per-instance `DebounceWindowMs` / `SpinnerWindowMs` /
///   `SpinnerThreshold` / `MaxAnnounceChars` parameters become
///   real (today the Stream profile is a pass-through).
/// - **8c** promotes `FileLogger` to a first-class channel so
///   `OutputEvent`s land in the rolling log structurally.
/// - **8d** adds the WASAPI Earcons channel + the Earcon
///   profile (replaces tech-plan §9).
/// - **8e** lifts the Stage-8 selection detection heuristic
///   into a `Selection` profile (replaces tech-plan §8).
/// - **8f** adds per-shell profile mapping via
///   `ShellRegistry`'s TOML extension.
///
/// The `Version: int = 1` + `Extensions: Map<string, obj>` pair
/// preserves forward-compatibility per spec B.2.3: a profile
/// entry written for a future device (e.g. v3 Monarch braille)
/// can round-trip through an older pty-speak that doesn't yet
/// know its fields, because the unknown fields land in
/// `Extensions` rather than being truncated.
[<RequireQualifiedAccess>]
type SemanticCategory =
    /// Coalesced text from the running shell (the everyday
    /// streaming-output case). Default in 8a; today the only
    /// `OutputBatch`-bearing producer is the Coalescer drain.
    | StreamChunk
    /// A selection prompt has appeared (gum-choose, fzf,
    /// Claude Code's Yes/No/Edit listbox). Producer ships in
    /// 8e (Selection profile).
    | SelectionShown
    /// A single item within a selection. Producer ships in 8e.
    | SelectionItem
    /// Selection prompt has been resolved (user picked an
    /// option, or pressed Esc). Producer ships in 8e.
    | SelectionDismissed
    /// Spinner frame from the shell. The Coalescer's
    /// per-`(rowIdx, hash)` + cross-row gates already
    /// suppress these today; the category exists so a future
    /// stage can route an explicit "spinner active" hint to
    /// the Earcon channel without going through the
    /// suppression path.
    | SpinnerTick
    /// Stderr-flagged or red-coloured output line. Distinct
    /// from `ParserError`: an `ErrorLine` is shell-emitted
    /// content that happens to be red (a build failure, an
    /// `npm ERR!` line); a `ParserError` is a pty-speak
    /// internal exception in the VT-parser path. Producer
    /// ships in a future stage (likely 8d Earcon profile or
    /// 8e Selection profile, depending on which lifts the
    /// red-text detection heuristic first).
    | ErrorLine
    /// Yellow / amber line. Producer ships alongside
    /// `ErrorLine`.
    | WarningLine
    /// Shell prompt redrawn (PS1 / PROMPT). Producer ships
    /// when OSC 133 prompt detection lands (Phase 2).
    | PromptDetected
    /// User submitted a command (pressed Enter at the prompt).
    /// Producer ships when input-side echo correlation lands
    /// (Stage 9 / Concern 3).
    | CommandSubmitted
    /// `BEL` (0x07) byte received. Producer ships in 8d
    /// alongside the Earcon channel.
    | BellRang
    /// `OSC 8` hyperlink emitted by the shell. Producer ships
    /// when OSC 8 detection lands (Phase 2).
    | HyperlinkOpened
    /// `DECSET 1049` — the shell entered the alt-screen
    /// (`vim`, `less`, full-screen TUI). 8a's drain emits
    /// this on `ModeChanged(AltScreen, true)`.
    | AltScreenEntered
    /// Other mode-flip barrier event (alt-screen exit,
    /// bracketed-paste toggle, focus-reporting toggle, DECCKM
    /// transition). 8a's drain emits this on every other
    /// `ModeChanged`.
    | ModeBarrier
    /// VtParser detected a malformed sequence; the reader-loop
    /// surfaced an unexpected exception. Per spec D.2 maps to
    /// `Priority.Background` — the Stream profile in 8a is a
    /// pass-through and does NOT honour Background-suppression
    /// yet, so `ParserError`s continue to reach NVDA via the
    /// `pty-speak.error` activity ID. The Background contract
    /// activates in 8b/8c when profiles + channels start
    /// reading `Priority`.
    | ParserError
    /// User-defined event category. The string is the stable
    /// identifier the producer chooses (e.g. a third-party
    /// Phase 2 extension's "git-prompt-segment"). The `Custom`
    /// payload routes via `Extensions` for any per-category
    /// metadata.
    | Custom of string

/// Priority lane the event speaks in. Per spec B.5.2's NVDA
/// mapping table:
///
/// | Priority | NVDA `NotificationProcessing` |
/// |---|---|
/// | `Interrupt` | `ImportantMostRecent` |
/// | `Assertive` | `ImportantAll` |
/// | `Polite` | `All` |
/// | `Background` | _suppressed at profile layer; never raised as UIA notification_ |
///
/// **Stage 8a does NOT consult `Priority`** in either the Stream
/// profile or the NvdaChannel — both pass through to the existing
/// `TerminalView.Announce(msg, activityId)` 2-arg overload, which
/// preserves the Stage-7 NotificationProcessing mapping
/// (`output → ImportantAll`, everything else → `MostRecent`).
/// The `Priority` field is recorded on every event so producers
/// can populate it accurately; consumers (Stream profile in 8b,
/// NvdaChannel in a later stage) start honouring it when their
/// time comes. The 8a deviation is deliberate: changing the
/// NotificationProcessing kind for `error` / `mode` events from
/// `MostRecent` to `ImportantMostRecent` is a behaviour change
/// that warrants its own NVDA-validation cycle.
[<RequireQualifiedAccess>]
type Priority =
    | Interrupt
    | Assertive
    | Polite
    | Background

/// Verbosity register the event is rendered at. Per spec B.7.2:
/// the same content can be rendered at different registers
/// depending on user interaction stage (Discovery / Navigation /
/// Selection / On-demand). 8a producers always emit `Precise`;
/// `Approximate` is wired through but underused until later
/// stages populate user-state awareness.
[<RequireQualifiedAccess>]
type VerbosityRegister =
    | Approximate
    | Precise

/// Producer + correlation metadata. `Producer` is a stable
/// short identifier ("drain", "coalescer", "selection-detector").
/// `Shell` is the active shell name (string-keyed for
/// cross-assembly portability; `Terminal.Pty.ShellRegistry.ShellId`
/// renders to one of "cmd" / "claude" / "powershell" today).
/// `Shell = None` is the 8a default — populating it becomes
/// load-bearing when 8f wires per-shell profile mapping.
/// `CorrelationId` links related events (e.g. a `SelectionShown`
/// + its subsequent `SelectionItem`s share one ID); 8a producers
/// emit `None`.
type SourceIdentity =
    { Producer: string
      Shell: string option
      CorrelationId: int64 option }

/// Spatial-audio hint. v1 channels (NVDA / FileLogger / Earcons)
/// don't consume this; v3 channels (spatial-audio engine) do.
/// 8a producers always emit `None`.
type SpatialHint =
    { /// -180 to +180 (degrees; 0 = front, +90 = right).
      Azimuth: float
      /// -90 to +90 (degrees; 0 = horizon).
      Elevation: float
      /// 0..1 (relative; consumed by spatial engines).
      Distance: float }

/// Multi-line refreshable braille hint. v1 channels don't
/// consume this; v3 multi-line braille channels (Monarch,
/// Dot Pad) do. 8a producers always emit `None`.
type RegionHint =
    { /// "header" / "body" / "footer" / arbitrary string.
      NamedRegion: string
      /// Within-region ordering hint.
      Order: int }

/// Streaming-queue navigation hint. Concern 3 territory; 8a
/// producers always emit `None`.
type StructuralRef =
    { ParentSegmentId: int64
      OrderInParent: int }

/// What a channel should render. `RenderText` is the 8a default
/// — a sanitised string the channel announces / writes / etc.
/// `RenderText2` admits the Approximate / Precise pair so a
/// channel can pick the right register without re-querying the
/// event. `RenderEarcon` is the 8d producer-side instruction
/// to play a named earcon. `RenderRaw` is the channel-specific
/// opaque payload escape hatch (e.g. UIA-listbox metadata in 8e).
type RenderInstruction =
    | RenderText of string
    | RenderText2 of approx: string * precise: string
    | RenderEarcon of earconId: string
    | RenderRaw of payload: obj

/// Channel identity. String-keyed so TOML profile entries
/// (Stage 8f) can refer to channels by name without leaking F#
/// types into user-facing config.
type ChannelId = string

/// Profile identity. Same rationale as `ChannelId`.
type ProfileId = string

/// A profile's verdict on one event: which channel renders it,
/// and how. A profile returns zero, one, or many decisions per
/// event — zero = "this profile drops this event"; one = "render
/// on this channel"; many = "fan out". 8a's pass-through Stream
/// profile always returns one decision per event (the NVDA
/// channel + a `RenderText` of the payload).
type ChannelDecision =
    { Channel: ChannelId
      Render: RenderInstruction }

/// The cross-channel typed event. See module docstring for the
/// `Version` + `Extensions` forward-compat contract. Producer
/// responsibilities (spec B.2.4):
///
/// 1. Set `Semantic` from the closed DU (or `Custom` with a
///    stable string ID).
/// 2. Set `Priority` based on the event's nature (spec D.2's
///    mapping table for the canonical `ScreenNotification`
///    cases).
/// 3. Set `Verbosity = Precise` unless the producer is an
///    approximate-mode emitter (none in 8a).
/// 4. Set `Source.Producer` to a stable identifier and
///    `Source.Shell` to the current shell from `ShellRegistry`
///    (8a passes `None`; 8f wires shell tracking).
/// 5. Run sanitisation through `AnnounceSanitiser.sanitise`
///    BEFORE placing the text in `Payload`. The PR-N contract
///    is the entry gate to the OutputEvent substrate.
/// 6. Set `Version = 1` and `Extensions = Map.empty`.
/// 7. Leave `SpatialHint` / `RegionHint` / `StructuralContext`
///    as `None` in v1 (no producer in v1 has the metadata to
///    populate them).
type OutputEvent =
    { Semantic: SemanticCategory
      Priority: Priority
      Verbosity: VerbosityRegister
      Source: SourceIdentity
      SpatialHint: SpatialHint option
      RegionHint: RegionHint option
      StructuralContext: StructuralRef option
      Payload: string
      Version: int
      Extensions: Map<string, obj> }

/// A channel is an `OutputEvent` consumer. The `Send` callback
/// is invoked by the dispatcher per `ChannelDecision`; it is
/// responsible for any threading marshalling (e.g., the
/// NvdaChannel's WPF dispatcher hop). Channels do NOT block
/// the dispatcher — backpressure is handled internally per
/// spec B.5.3.
type Channel =
    { Id: ChannelId
      Send: OutputEvent -> RenderInstruction -> unit }

/// A profile is a pure function `OutputEvent → ChannelDecision[]`
/// with per-instance state. `Reset` is invoked when the active
/// shell changes (8f) so the profile can clear any
/// per-shell-session caches; 8a's pass-through Stream profile
/// has no state to reset, but the contract is in place.
type Profile =
    { Id: ProfileId
      Apply: OutputEvent -> ChannelDecision[]
      Reset: unit -> unit }

/// 8a OutputEvent default — convenient constructor for the
/// hot path. Caller fills in the differing fields (`Semantic`,
/// `Priority`, `Payload`, `Source.Producer`); the rest stays
/// at v1 defaults. The companion-module + record share the same
/// name via `CompilationRepresentationFlags.ModuleSuffix`, which
/// the F# compiler uses to internally rename the module so the
/// pair compiles without a name clash. Same pattern F# Core
/// uses for `Option` / `List` / `Map`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module OutputEvent =
    let internal defaultSource: SourceIdentity =
        { Producer = ""
          Shell = None
          CorrelationId = None }

    /// Construct an OutputEvent with the v1 defaults pre-filled.
    /// `producer` is the stable producer identifier;
    /// `payload` is the already-sanitised text.
    let create
        (semantic: SemanticCategory)
        (priority: Priority)
        (producer: string)
        (payload: string)
        : OutputEvent
        =
        { Semantic = semantic
          Priority = priority
          Verbosity = VerbosityRegister.Precise
          Source = { defaultSource with Producer = producer }
          SpatialHint = None
          RegionHint = None
          StructuralContext = None
          Payload = payload
          Version = 1
          Extensions = Map.empty }
