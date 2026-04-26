namespace Terminal.Core

/// Marker type used by smoke tests to verify the assembly loads.
type Marker = class end

/// A single screen cell. Stage 2 fills in the real fields.
type Cell = { Placeholder: unit }

/// The screen buffer. Stage 2 fills in the real fields.
type ScreenBuffer = { Placeholder: unit }

/// VT events emitted by the parser. Stage 3 expands this DU.
type VtEvent =
    | Placeholder

/// Bus messages routed between subsystems. Stages 4-9 expand this DU.
type BusMessage =
    | Placeholder

/// Earcons (audio cues). Stage 7 expands this DU.
type Earcon =
    | Placeholder

/// Accessibility markers / regions. Stage 6 expands this DU.
type AccessibilityMarker =
    | Placeholder
