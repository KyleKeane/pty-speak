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
/// **Cycle 37b (Stage 8e-B peer cutover).** Apply emits TWO
/// ChannelDecisions per Selection event: a FileLogger-channel
/// `RenderText` (audit trail) and an NVDA-channel `RenderRaw`
/// payload of type `SelectionRawPayload` carrying the UIA
/// listbox metadata. The 37a interim NVDA-channel `RenderText`
/// decision has been dropped now that
/// `TerminalListAutomationPeer` (in `Terminal.Accessibility`)
/// takes over the announce semantics — NVDA hears the list as
/// a real `ControlType.List` with `1 of 4` semantics, not as
/// rendered plain text. The FileLogger text decision remains
/// for the audit trail (paste-back debugging post-incident).
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

    /// Build a FileLogger-channel decision carrying the supplied
    /// text. The detector's payloads are already sanitised via
    /// `AnnounceSanitiser.sanitise`; the rendered text we add
    /// here is built from those sanitised pieces, so it inherits
    /// the cleanliness.
    let private fileLoggerDecision (text: string) : ChannelDecision =
        { Channel = FileLoggerChannel.id
          Render = RenderText text }

    /// Cycle 37a — build an NVDA-channel decision carrying the
    /// `SelectionRawPayload` UIA-free snapshot. NvdaChannel
    /// routes this to `marshalRawPayload`, which the composition
    /// root binds to `TerminalView.AnnounceRawPayload` (37a stub
    /// in the View; 37b promotes to peer-state update).
    ///
    /// `:>` upcast (preserves non-null) rather than `box`
    /// (F# 9 nullness annotates `box: 'T -> obj | null`, which
    /// can't satisfy `RenderRaw of payload: obj`). Mirrors the
    /// NvdaChannelTests fixture pattern.
    let private rawDecision (payload: SelectionRawPayload) : ChannelDecision =
        { Channel = NvdaChannel.id
          Render = RenderRaw (payload :> obj) }

    /// Construct the decisions array for a single Selection event.
    /// Cycle 37b emits TWO decisions: FileLogger `RenderText` for
    /// the audit trail (paste-back debugging) + NVDA `RenderRaw`
    /// carrying the UIA payload for `TerminalListAutomationPeer`
    /// to consume. The 37a-interim NVDA `RenderText` decision was
    /// dropped now that the peer takes over the announce
    /// semantics — keeping it would cause double-announce (text
    /// + listbox).
    let private selectionDecisions
            (text: string)
            (payload: SelectionRawPayload)
            : ChannelDecision[] =
        [| fileLoggerDecision text
           rawDecision payload |]

    /// Cycle 37a — build the raw payload for a `SelectionShown`
    /// event. Pulls `AllItems` + `ItemCount` + `SelectedIndex`
    /// from Extensions; `ItemIndex` is -1 (per the SelectionShown
    /// invariant — the burst's per-item index belongs to
    /// SelectionItem events). Missing-data fallback uses -1 for
    /// `SelectedIndex` so the peer can detect malformed events
    /// and skip raising the focus event rather than guessing.
    let private buildShownPayload (event: OutputEvent) : SelectionRawPayload =
        let allItems =
            tryReadStringArray event SelectionExtensions.AllItems
            |> Option.defaultValue [||]
        let count =
            tryReadInt event SelectionExtensions.ItemCount
            |> Option.defaultValue allItems.Length
        let selectedIdx =
            tryReadInt event SelectionExtensions.SelectedIndex
            |> Option.defaultValue -1
        { Kind = "shown"
          ItemCount = count
          SelectedIndex = selectedIdx
          ItemIndex = -1
          AllItems = allItems
          ItemText = "" }

    /// Cycle 37a — build the raw payload for a `SelectionItem`
    /// event. Pulls `ItemText`, `ItemIndex`, `SelectedIndex`,
    /// `ItemCount` from Extensions. `AllItems` is `[||]`
    /// (SelectionItem events don't repeat the full list — the
    /// 37b peer caches it from the most recent SelectionShown).
    let private buildItemPayload (event: OutputEvent) : SelectionRawPayload =
        let text =
            tryReadString event SelectionExtensions.ItemText
            |> Option.defaultValue ""
        let itemIdx =
            tryReadInt event SelectionExtensions.ItemIndex
            |> Option.defaultValue -1
        let selectedIdx =
            tryReadInt event SelectionExtensions.SelectedIndex
            |> Option.defaultValue -1
        let count =
            tryReadInt event SelectionExtensions.ItemCount
            |> Option.defaultValue 0
        { Kind = "item"
          ItemCount = count
          SelectedIndex = selectedIdx
          ItemIndex = itemIdx
          AllItems = [||]
          ItemText = text }

    /// Cycle 37a — sentinel payload for `SelectionDismissed`.
    /// All numeric fields default to -1 / 0; the 37b peer uses
    /// `Kind = "dismissed"` to drop its child peer + raise
    /// `StructureChanged`.
    let private dismissedPayload : SelectionRawPayload =
        { Kind = "dismissed"
          ItemCount = 0
          SelectedIndex = -1
          ItemIndex = -1
          AllItems = [||]
          ItemText = "" }

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
                    let payload = buildShownPayload event
                    [| event, selectionDecisions (renderShown event) payload |]
                | SemanticCategory.SelectionItem ->
                    let payload = buildItemPayload event
                    [| event, selectionDecisions (renderItem event) payload |]
                | SemanticCategory.SelectionDismissed ->
                    [| event, selectionDecisions "selection dismissed" dismissedPayload |]
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
