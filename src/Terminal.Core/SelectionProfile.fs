namespace Terminal.Core

/// Stage 8e-A part 2 (Cycle 29b) — Selection profile. Consumes
/// `OutputEvent`s emitted by `SelectionDetector` and routes them
/// to the NVDA + FileLogger channels as `RenderText` payloads.
/// Cycle 29a shipped the detector substrate fully unit-tested
/// but not wired; 29b ships this consumer + the `Program.fs`
/// wiring that connects detector output to the dispatcher.
///
/// **Empty-payload trick** (mirrors 8d.2 `EarconProfile` for
/// `ErrorLine` / `WarningLine`). The detector emits Selection
/// events with empty `Payload`; user-facing text is constructed
/// at render time by THIS profile from the structured
/// `Extensions` data. Reason: `PassThroughProfile.Apply` is a
/// catch-all that fans every event to NVDA + FileLogger via
/// `RenderText event.Payload`. If the detector emitted
/// non-empty Payload, NVDA would announce the text TWICE — once
/// from the catch-all, once from this profile. Empty Payload
/// makes `NvdaChannel` skip the `RenderText ""` path
/// (`NvdaChannel.fs:87`); this profile then supplies the
/// user-facing text and announces it ONCE. FileLogger gets two
/// entries per event (one with empty Payload + structured
/// Extensions, one with the rendered text) — the slight noise
/// is acceptable for the audit trail.
///
/// **Apply mapping (29b text-only).**
/// - `SelectionShown` → "selection prompt: %s (selected: %s)"
///   constructed from `Extensions[AllItems]` +
///   `Extensions[SelectedIndex]`. Routed to NVDA + FileLogger.
/// - `SelectionItem` → "%s, %d of %d" or "selected: %s, %d of
///   %d" depending on `(ItemIndex == SelectedIndex)`. Routed to
///   NVDA + FileLogger.
/// - `SelectionDismissed` → "selection dismissed". Routed to
///   NVDA + FileLogger.
/// - All other Semantic categories → return `[||]` (purely
///   additive observer for the events it claims; mirrors the
///   `EarconProfile` pattern exactly).
///
/// **What 8e-B will change.** The UIA listbox peer arrives in
/// Stage 8e-B, lifting the text-only `RenderText` decisions to
/// `RenderRaw` payloads carrying UIA listbox metadata
/// (`SelectionExtensions.AllItems` / `TopRow` / `BottomRow`
/// already populated by the detector for this purpose). NVDA
/// will then read the list as a real listbox with `1 of 4` /
/// `2 of 4` semantics from UIA's `IItemContainerProvider`
/// instead of from this profile's plain-text rendering.
///
/// **No Parameters record.** Profile is stateless (the tunable
/// thresholds live on `SelectionDetector.Parameters`, not here).
/// Mirrors the `EarconProfile.create () : Profile` signature.
/// Cycle 29c will load detector parameters from
/// `[profile.selection]` TOML; the profile itself remains
/// parameterless.
///
/// **Spec reference.** `spec/event-and-output-framework.md` Part
/// B.3.2 (Selection profile row) + Part B.3.3 (Profile signature
/// — `EarconProfile` cousin pattern).
module SelectionProfile =

    /// Stable profile identifier registered with the dispatcher's
    /// `ProfileRegistry`. Cycle 29c's TOML loader keys
    /// `[profile.selection]` on this string for parameter
    /// overrides.
    [<Literal>]
    let id: ProfileId = "selection"

    /// Read an int value from `OutputEvent.Extensions` for the
    /// given key. Returns `None` if absent or non-int. The
    /// detector always sets these for SelectionShown /
    /// SelectionItem events, so `None` only fires for malformed
    /// upstream events (or test fixtures); the profile falls
    /// back to a sane default in that case to avoid throwing.
    let private tryReadInt (event: OutputEvent) (key: string) : int option =
        match Map.tryFind key event.Extensions with
        | Some value ->
            match value with
            | :? int as i -> Some i
            | _ -> None
        | None -> None

    /// Read a string value from `Extensions`. Same `None`
    /// behaviour as `tryReadInt`.
    let private tryReadString (event: OutputEvent) (key: string) : string option =
        match Map.tryFind key event.Extensions with
        | Some value ->
            match value with
            | :? string as s -> Some s
            | _ -> None
        | None -> None

    /// Read a `string[]` value from `Extensions`. Same `None`
    /// behaviour as `tryReadInt`.
    let private tryReadStringArray (event: OutputEvent) (key: string) : string[] option =
        match Map.tryFind key event.Extensions with
        | Some value ->
            match value with
            | :? (string[]) as arr -> Some arr
            | _ -> None
        | None -> None

    /// Build an NVDA-channel decision carrying the supplied
    /// already-sanitised text. Mirrors `EarconProfile`'s
    /// `earconDecision` helper.
    let private nvdaDecision (text: string) : ChannelDecision =
        { Channel = NvdaChannel.id
          Render = RenderText text }

    /// Build a FileLogger-channel decision carrying the supplied
    /// text. The detector's payloads are already sanitised via
    /// `AnnounceSanitiser.sanitise`; the rendered text we add
    /// here is built from those sanitised pieces, so it inherits
    /// the cleanliness.
    let private fileLoggerDecision (text: string) : ChannelDecision =
        { Channel = FileLoggerChannel.id
          Render = RenderText text }

    /// Construct the decisions array for a single Selection event.
    /// Both NVDA and FileLogger receive the same text so a
    /// paste-back of the FileLogger log carries the same semantic
    /// content NVDA spoke.
    let private selectionDecisions (text: string) : ChannelDecision[] =
        [| nvdaDecision text
           fileLoggerDecision text |]

    /// Render the user-facing text for a `SelectionShown` event.
    /// Pulls `AllItems` + `SelectedIndex` from Extensions and
    /// formats: "selection prompt: Edit, Yes, Always, No
    /// (selected: Yes)". Falls back to a count-only summary if
    /// the structured data is missing.
    let private renderShown (event: OutputEvent) : string =
        let items =
            tryReadStringArray event SelectionExtensions.AllItems
            |> Option.defaultValue [||]
        let count =
            tryReadInt event SelectionExtensions.ItemCount
            |> Option.defaultValue items.Length
        let selectedIdx =
            tryReadInt event SelectionExtensions.SelectedIndex
            |> Option.defaultValue 0
        if items.Length = 0 then
            sprintf "selection prompt, %d items" count
        else
            let joined = String.concat ", " items
            if selectedIdx >= 0 && selectedIdx < items.Length then
                sprintf
                    "selection prompt: %s (selected: %s)"
                    joined
                    items.[selectedIdx]
            else
                sprintf "selection prompt: %s" joined

    /// Render the user-facing text for a `SelectionItem` event.
    /// Pulls `ItemText`, `ItemIndex`, `SelectedIndex`, and
    /// `ItemCount` from Extensions and formats: "Yes, 2 of 4"
    /// or "selected: Yes, 2 of 4" depending on whether THIS item
    /// is the selected one.
    let private renderItem (event: OutputEvent) : string =
        let text =
            tryReadString event SelectionExtensions.ItemText
            |> Option.defaultValue ""
        let itemIdx =
            tryReadInt event SelectionExtensions.ItemIndex
            |> Option.defaultValue 0
        let selectedIdx =
            tryReadInt event SelectionExtensions.SelectedIndex
            |> Option.defaultValue 0
        let count =
            tryReadInt event SelectionExtensions.ItemCount
            |> Option.defaultValue 0
        if itemIdx = selectedIdx then
            sprintf "selected: %s, %d of %d" text (itemIdx + 1) count
        else
            sprintf "%s, %d of %d" text (itemIdx + 1) count

    /// Construct the Selection profile. Stateless (no per-instance
    /// state in 29b); mirrors `EarconProfile.create ()` shape.
    /// 8e-B will introduce `RenderRaw` UIA listbox metadata; the
    /// profile signature stays the same — only the `Apply`
    /// implementation grows.
    let create () : Profile =
        { Id = id
          Apply =
            fun event ->
                match event.Semantic with
                | SemanticCategory.SelectionShown ->
                    [| event, selectionDecisions (renderShown event) |]
                | SemanticCategory.SelectionItem ->
                    [| event, selectionDecisions (renderItem event) |]
                | SemanticCategory.SelectionDismissed ->
                    [| event, selectionDecisions "selection dismissed" |]
                | _ ->
                    // No decision for other Semantic categories.
                    [||]
          Tick =
            fun _ ->
                // No time-driven flush — selection events are
                // event-driven only.
                [||]
          Reset =
            fun () -> () }
