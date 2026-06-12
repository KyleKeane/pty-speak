namespace Engine.Core

open Engine.Core.EngineEvent

/// ADR 0012 S3 — deterministic spatial-audio signatures for the
/// universal event bus (RELAUNCH-SPEC §7.1 device 2 + §7.3).
/// Every engine event maps to a stereo-stage cue; the stage
/// layout encodes the attention contract by position:
///
///   far left −0.8        lifecycle (session)
///   left     −0.5        diagnostics (notes)
///   center    0.0        the narrative thread (request,
///                        completion) — foreground lives where
///                        the voice lives
///   near right +0.35     content trickle (chunk seals;
///                        pitch identifies the ChunkKind)
///   right    +0.6        turn progress (pitch rises with the
///                        count — progression is audible)
///
/// Navigation cues are direction-coded: next/previous pan
/// toward the movement, descend is pitched low, ascend high;
/// an edge is a dull low tone on the side of the attempt.
///
/// Pure data + total functions. Renderers (Engine.Audio's
/// stereo panner today; an HRTF renderer later) consume `Cue`
/// — the model carries stage position, never implementation.
module SpatialCue =

    /// One spatial-audio signature. `Pan` is −1.0 (hard left)
    /// … 0.0 (center) … +1.0 (hard right); `Pitch` in Hz;
    /// `Gain` 0.0–1.0 (cues sit under speech, so most are
    /// well below 1).
    type Cue =
        { Pan: float
          Pitch: float
          DurationMs: int
          Gain: float }

    /// Unique pitch identity per chunk kind (ADR 0012 S3 —
    /// the ear can tell WHAT kind of content just sealed).
    /// Frequencies are distinct musical pitches spread across
    /// the 300–1000 Hz earcon-friendly band.
    let pitchForKind (kind: Chunk.ChunkKind) : float =
        match kind with
        | Chunk.Heading _ -> 988.0 // B5 — sections ring highest
        | Chunk.Paragraph -> 523.0 // C5
        | Chunk.ListBlock _ -> 587.0 // D5
        | Chunk.ListItem -> 659.0 // E5
        | Chunk.CodeBlock _ -> 698.0 // F5
        | Chunk.BlockQuote -> 494.0 // B4
        | Chunk.ThematicBreak -> 350.0
        | Chunk.UserRequest -> 660.0
        | Chunk.ToolUse _ -> 784.0 // G5
        | Chunk.ToolResult false -> 740.0 // F#5
        | Chunk.ToolResult true -> 311.0 // D#4 — errors ring low
        | Chunk.AgentError -> 233.0 // A#3
        | Chunk.SystemNote -> 415.0 // G#4

    /// The cue for one engine event; `None` = deliberately
    /// silent (no event is silent today, but the policy seam
    /// is total and explicit).
    let forEvent (event: EngineEvent.EngineEvent) : Cue option =
        match event with
        | RequestCaptured _ ->
            // Center stage, bright: your words entered the tree.
            Some { Pan = 0.0; Pitch = 660.0; DurationMs = 70; Gain = 0.35 }
        | SessionStarted _ ->
            Some { Pan = -0.8; Pitch = 392.0; DurationMs = 120; Gain = 0.3 }
        | ChunkSealed chunk ->
            // The content trickle: a soft per-kind tick near
            // right — ambient awareness of WHAT is landing
            // without a single spoken word (§5.2).
            Some { Pan = 0.35
                   Pitch = pitchForKind chunk.Kind
                   DurationMs = 35
                   Gain = 0.18 }
        | ResponseProgress count ->
            // Rising series on the right: each ambient progress
            // step is audibly "further along".
            let capped = min count 12
            Some { Pan = 0.6
                   Pitch = 440.0 + 20.0 * float capped
                   DurationMs = 45
                   Gain = 0.2 }
        | ResponseCompleted (false, _) ->
            Some { Pan = 0.0; Pitch = 880.0; DurationMs = 140; Gain = 0.4 }
        | ResponseCompleted (true, _) ->
            Some { Pan = 0.0; Pitch = 220.0; DurationMs = 260; Gain = 0.45 }
        | EngineNote _ ->
            Some { Pan = -0.5; Pitch = 330.0; DurationMs = 80; Gain = 0.25 }

    /// Navigation verbs, for direction-coded cues.
    type NavDirection =
        | Next
        | Previous
        | Descend
        | Ascend
        | Jump
        | ReturnToAnchor

    let private sideOf (direction: NavDirection) : float =
        match direction with
        | Next -> 0.3
        | Previous -> -0.3
        | Jump -> 0.5
        | ReturnToAnchor -> -0.5
        | Descend | Ascend -> 0.0

    /// The cue for a navigation outcome: a crisp tick panned
    /// toward the movement (descend low / ascend high); a dull
    /// low tone on the side of a refused move (the §6.4 edge —
    /// you hear WHERE you bumped before the voice says why).
    let forNav (direction: NavDirection) (moved: bool) : Cue =
        if moved then
            let pitch =
                match direction with
                | Descend -> 392.0 // down in space, down in pitch
                | Ascend -> 587.0
                | _ -> 466.0
            { Pan = sideOf direction
              Pitch = pitch
              DurationMs = 30
              Gain = 0.22 }
        else
            { Pan = sideOf direction
              Pitch = 196.0
              DurationMs = 90
              Gain = 0.3 }
