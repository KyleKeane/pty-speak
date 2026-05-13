namespace Terminal.Accessibility

open System
open System.Text
open System.Windows
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open System.Windows.Automation.Text
open Terminal.Core

// Audit-cycle PR-C — restrict accessibility types to the
// internal callers that actually use them. The Stage 4 design
// shipped them as `public` because F# defaults to public, but
// the only consumer is `PtySpeak.Views` (the C# WPF library
// that constructs the peer in `TerminalView.OnCreateAutomationPeer`).
// Marking them `internal` + exposing to Views via
// `InternalsVisibleTo` prevents accidental third-party API
// dependency on these types and gives Stage 5+ contributors
// the freedom to break their signatures without an external
// breaking-change concern.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Views")>]
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PtySpeak.Tests.Unit")>]
// Cycle 46 PR-C — Terminal.App needs to call
// `TerminalAutomationPeer.RaiseCaretMovedToTail` on tuple
// finalise (alongside the `Announce` call — see the helper's
// doc-comment + ADR 0002 §"Status notes" for the Option ★★
// Augment resolution). The peer type stays `internal`; the
// assembly attribute opens the surface to the composition
// root without widening the public API.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Terminal.App")>]
do ()

/// Stage 4 — UIA peer that exposes `TerminalView` to the WPF
/// Automation tree as a Document with the Text pattern.
///
/// The architectural path was settled by PR #56:
///
///   * `protected virtual GetPatternCore` is unreachable from
///     external assemblies in the .NET 9 WPF reference set
///     (CS0117 / FS0855), so the spike-era plan to add patterns
///     by overriding it was a dead end.
///   * `WM_GETOBJECT` interception with a custom
///     `IRawElementProviderSimple` works for legacy MSAA
///     (`OBJID_CLIENT`) but breaks UIA3 (`UiaRootObjectId`):
///     UIA3 expects an `IRawElementProviderFragmentRoot` there,
///     and a simple provider can't supply the fragment-root
///     navigation surface — CI regressed when we matched
///     `UiaRootObjectId`. (Audit-cycle PR-C deleted the dead
///     `WindowSubclassNative` + `TerminalRawProvider` files
///     that were kept "just in case" after the pivot; if you
///     ever need that path back, see git history before this
///     PR for the implementation.)
///   * `public virtual GetPattern(PatternInterface)` IS
///     reachable from external assemblies (it's the public
///     entry point that calls `GetPatternCore` internally).
///     Overriding it lets us add patterns without ever
///     touching the unreachable protected member, and the
///     pattern is added to the SAME peer that's already in
///     WPF's tree, so navigation, focus, and properties keep
///     working unchanged.
///
/// `textProvider` is supplied by the owner (`TerminalView`) so
/// the peer doesn't have to know about the screen-snapshot
/// machinery; the view holds the closure over its own `_screen`
/// field and the peer just hands the provider through to UIA.
///
/// **Cycle 37b** — constructor extended with `writePtyBytes:
/// Action<byte[]>` so child `TerminalListItemAutomationPeer`
/// instances can fire `IInvokeProvider.Invoke()` → `\r` byte
/// onto the PTY (Claude tool-use prompt accepts the highlighted
/// choice on Enter). The View's `OnCreateAutomationPeer` passes
/// `this.WritePtyBytes` (a public method that wraps the
/// View's private `_writeBytes` field).
type internal TerminalListAutomationPeer
    (parent: AutomationPeer,
     initialPayload: SelectionRawPayload,
     writePtyBytes: Action<byte[]>) =
    inherit AutomationPeer()

    let mutable selectedIndex : int = initialPayload.SelectedIndex
    let itemCount : int = initialPayload.ItemCount
    let allItems : string[] = initialPayload.AllItems

    /// ListItem peers built lazily on first access (typically
    /// from the UIA-thread `GetChildrenCore` call) and cached
    /// for the list's lifetime. Avoiding the `as this` /
    /// class-let-binding initialization-soundness pattern
    /// (FS0021 under TreatWarningsAsErrors); deferring
    /// construction to a `member` ensures `this` is fully bound
    /// when the items are built.
    let mutable cachedItems : TerminalListItemAutomationPeer[] | null = null

    member private this.EnsureItems() : TerminalListItemAutomationPeer[] =
        match cachedItems with
        | null ->
            let arr =
                allItems
                |> Array.mapi (fun i text ->
                    TerminalListItemAutomationPeer(this, text, i, writePtyBytes))
            cachedItems <- arr
            arr
        | arr -> arr

    member internal _.SelectedIndex
        with get () = selectedIndex
        and set v = selectedIndex <- v

    member internal _.ItemCount = itemCount

    /// Called by the parent peer when a SelectionItem event
    /// arrives. Mutates state + raises the per-item selection
    /// event so NVDA shifts focus to the new selected item.
    member this.UpdateSelection(newSelectedIndex: int) =
        let items = this.EnsureItems()
        selectedIndex <- newSelectedIndex
        if newSelectedIndex >= 0 && newSelectedIndex < items.Length then
            let target = items.[newSelectedIndex]
            target.RaiseAutomationEvent(
                AutomationEvents.SelectionItemPatternOnElementSelected)

    interface ISelectionProvider with
        member _.CanSelectMultiple = false
        member _.IsSelectionRequired = true
        member this.GetSelection() : IRawElementProviderSimple[] =
            // F# interface members typed-as-the-implementing
            // class via `member this.X`; direct call to private
            // `EnsureItems` + protected-instance
            // `ProviderFromPeer` (inherited from AutomationPeer)
            // both work without downcast.
            let items = this.EnsureItems()
            if selectedIndex >= 0 && selectedIndex < items.Length then
                let peer = items.[selectedIndex] :> AutomationPeer
                let provider = this.ProviderFromPeer(peer)
                [| provider |]
            else
                Array.empty<IRawElementProviderSimple>

    override this.GetChildrenCore() =
        let items = this.EnsureItems()
        let list = ResizeArray<AutomationPeer>(items.Length)
        for p in items do
            list.Add(p :> AutomationPeer)
        list

    override _.GetClassNameCore() = "TerminalList"
    override _.GetAutomationControlTypeCore() = AutomationControlType.List
    override _.GetNameCore() = "Selection prompt"
    override _.IsContentElementCore() = true
    override _.IsControlElementCore() = true

    // Remaining AutomationPeer abstract overrides. The list peer
    // is virtual (no FrameworkElement backing) so geometry +
    // focusability concepts don't directly apply; safe defaults
    // mirror the document peer's behaviour delegated through
    // FrameworkElementAutomationPeer in the parent.
    override _.GetBoundingRectangleCore() = System.Windows.Rect.Empty
    override _.GetClickablePointCore() = System.Windows.Point()
    override _.HasKeyboardFocusCore() = false
    override _.IsEnabledCore() = true
    override _.IsKeyboardFocusableCore() = true
    override _.IsOffscreenCore() = false
    override _.IsPasswordCore() = false
    override _.IsRequiredForFormCore() = false
    override _.SetFocusCore() = ()

    // String-valued metadata abstracts. Empty strings match the
    // .NET 9 convention for "no value" — UIA clients (NVDA,
    // Inspect.exe) interpret as unset rather than the literal
    // empty string.
    override _.GetAcceleratorKeyCore() = ""
    override _.GetAccessKeyCore() = ""
    override _.GetAutomationIdCore() = "TerminalList"
    override _.GetHelpTextCore() = ""
    override _.GetItemStatusCore() = ""
    override _.GetItemTypeCore() = ""
    // No labeled-by relationship; AutomationPeer's documented
    // semantic for "none" is null. F# 9's view of the WPF
    // reference assembly types `AutomationPeer` (rather than
    // `AutomationPeer | null`); `Unchecked.defaultof<_>`
    // produces null at runtime without the strict-nullness
    // diagnostic.
    override _.GetLabeledByCore() = Unchecked.defaultof<AutomationPeer>
    override _.GetOrientationCore() = AutomationOrientation.None

    // GetParent() is non-virtual on AutomationPeer (FS0855). The
    // parent relationship is established via the document peer's
    // GetChildrenCore returning this list peer; UIA's tree
    // walker handles the upward walk via internal framework
    // bookkeeping.

    override this.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.Selection ->
            let provider = this :> ISelectionProvider
            let result : obj | null = provider
            result
        | _ -> null

/// Cycle 37b — virtual UIA peer for a single item within a
/// detected selection list. Implements `ISelectionItemProvider`
/// (NVDA's "is this the selected one?" interrogation +
/// PositionInSet/SizeOfSet) and `IInvokeProvider` (single-key
/// activation: NVDA in focus mode pressing Enter on the
/// selected item writes `\r` to the PTY, which Claude
/// interprets as "press the highlighted choice"). Per
/// `docs/CANONICAL-DISPLAY-CATALOG.md` §2.14 ConfirmationPrompt
/// hybrid contract.
///
/// `parent` is `TerminalListAutomationPeer` so this peer can
/// query the parent's mutable `SelectedIndex` (no per-item
/// state; the listbox owns the cursor). Mutual recursion via
/// `and` resolves the forward reference from the parent's
/// `itemPeers` field.
and internal TerminalListItemAutomationPeer
    (parent: TerminalListAutomationPeer,
     text: string,
     index: int,
     writePtyBytes: Action<byte[]>) =
    inherit AutomationPeer()

    interface ISelectionItemProvider with
        member _.IsSelected = parent.SelectedIndex = index
        member this.SelectionContainer =
            this.ProviderFromPeer(parent :> AutomationPeer)
        // Selection mutation from UIA is read-only in 37b — the
        // PTY drives selection via arrow-key echoes, which the
        // detector re-fires as SelectionItem events. Stage 8e-C
        // generalizes this to UIA-driven Select() that writes
        // arrow bytes to the PTY.
        member _.Select() = ()
        member _.AddToSelection() = ()
        member _.RemoveFromSelection() = ()

    interface IInvokeProvider with
        member _.Invoke() =
            // Send Enter byte (`\r` = 0x0D) to PTY. Claude's
            // tool-use prompt accepts the highlighted choice on
            // Enter. cmd `choice` and other shells with
            // different activation keys are out of scope for
            // 37b (SelectionDetector is shellKey-gated to
            // "claude").
            writePtyBytes.Invoke([| 0x0Duy |])

    override _.GetClassNameCore() = "TerminalListItem"
    override _.GetAutomationControlTypeCore() = AutomationControlType.ListItem
    override _.GetNameCore() = text
    override _.IsContentElementCore() = true
    override _.IsControlElementCore() = true
    override _.GetPositionInSetCore() = index + 1
    override _.GetSizeOfSetCore() = parent.ItemCount

    // Remaining AutomationPeer abstract overrides. ListItem peers
    // are virtual; focus semantics defer to the PTY-side cursor.
    override _.GetBoundingRectangleCore() = System.Windows.Rect.Empty
    override _.GetClickablePointCore() = System.Windows.Point()
    override _.HasKeyboardFocusCore() = false
    override _.IsEnabledCore() = true
    override _.IsKeyboardFocusableCore() = true
    override _.IsOffscreenCore() = false
    override _.IsPasswordCore() = false
    override _.IsRequiredForFormCore() = false
    override _.SetFocusCore() = ()

    // ListItem leaf-node: no children. Empty list (not null)
    // matches the .NET 9 non-null `List<AutomationPeer>` return
    // type per the F# 9 view of the WPF reference assembly.
    override _.GetChildrenCore() = System.Collections.Generic.List<AutomationPeer>()

    // String-valued metadata abstracts. ItemText is reserved
    // for the ListItem's name; AutomationId differentiates
    // siblings within the parent list peer.
    override _.GetAcceleratorKeyCore() = ""
    override _.GetAccessKeyCore() = ""
    override _.GetAutomationIdCore() = sprintf "TerminalListItem[%d]" index
    override _.GetHelpTextCore() = ""
    override _.GetItemStatusCore() = ""
    override _.GetItemTypeCore() = ""
    override _.GetLabeledByCore() = Unchecked.defaultof<AutomationPeer>
    override _.GetOrientationCore() = AutomationOrientation.None

    // GetParent() is non-virtual on AutomationPeer (FS0855). The
    // parent relationship is established via the list peer's
    // GetChildrenCore returning this item peer.

    override this.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.SelectionItem ->
            let provider = this :> ISelectionItemProvider
            let result : obj | null = provider
            result
        | PatternInterface.Invoke ->
            let provider = this :> IInvokeProvider
            let result : obj | null = provider
            result
        | _ -> null

/// Stage 4 / Cycle 37b — UIA peer that exposes `TerminalView`
/// to the WPF Automation tree as a Document with the Text
/// pattern, plus (Cycle 37b) child `TerminalListAutomationPeer`
/// instances when a Claude tool-use selection prompt is active.
type internal TerminalAutomationPeer
    (owner: FrameworkElement,
     textProvider: ITextProvider,
     writePtyBytes: Action<byte[]>) =
    inherit FrameworkElementAutomationPeer(owner)

    /// Cycle 37b — currently-active list peer, materialized when
    /// `UpdateSelectionState` receives a `"shown"` payload + dropped
    /// when it receives a `"dismissed"` payload. The peer's
    /// presence drives `IsContentElementCore` (false while
    /// active per spec §8.5 dedup) and `GetChildrenCore` (returns
    /// the list peer as the sole child while active).
    let mutable currentListPeer : TerminalListAutomationPeer option = None

    // Cycle 46 PR-B — ControlType is `Edit` (was `Document`
    // pre-Cycle-46) so NVDA treats TerminalView as a
    // text-edit surface. Combined with the substrate swap of
    // the ITextProvider (screen grid → ContentHistory; see
    // `ContentHistoryTextRange.fs`), NVDA's native text-edit
    // reading path (Insert+Down read-all, Up/Down line nav,
    // Ctrl+End jump-to-end) operates against the
    // ContentHistory tail.
    //
    // This flip is the load-bearing change for the Cycle 46
    // typing-interrupts-speech win: NVDA's "Speech interrupt
    // for typed character" setting fires on any key press
    // inside an Edit, regardless of how the in-flight speech
    // was initiated. See ADR 0002 + the
    // `RaiseCaretMovedToTail` doc-comment below for the
    // channel-side caret-event signal that complements this.
    override _.GetAutomationControlTypeCore() = AutomationControlType.Edit
    override _.GetClassNameCore() = "TerminalView"
    override _.GetNameCore() = "Terminal"
    override _.IsControlElementCore() = true

    /// Cycle 37b — full-document content-element suppression
    /// while a list peer is materialized. Per
    /// `spec/tech-plan.md` §8.5 dedup: NVDA reads the list peer
    /// (and only the list peer) for the selection rows. The
    /// pragmatic full-document form (chosen 2026-05-10) trades
    /// off NVDA reading-cursor history browse during a prompt;
    /// per-range exclusion via `GetVisibleRanges` can iterate
    /// post-merge if the trade-off bites.
    override _.IsContentElementCore() =
        match currentListPeer with
        | Some _ -> false
        | None -> true

    /// Cycle 37b — return the active list peer as the sole
    /// child while a selection is active; defer to base
    /// implementation otherwise. This is the hook that makes
    /// the virtual list peer visible in NVDA's UIA tree walk.
    override this.GetChildrenCore() =
        match currentListPeer with
        | Some lp ->
            let list = ResizeArray<AutomationPeer>(1)
            list.Add(lp :> AutomationPeer)
            list
        | None -> base.GetChildrenCore()

    /// Cycle 37b — promotes the 37a stub to peer-state update.
    /// Called from `TerminalView.AnnounceRawPayload` on the WPF
    /// UI thread (via the 37a `Dispatcher.Invoke` wrapper).
    /// Mutates `currentListPeer` + raises StructureChanged on
    /// the parent + delegates per-item selection to the active
    /// list peer.
    member this.UpdateSelectionState(payload: SelectionRawPayload) =
        match payload.Kind with
        | "shown" ->
            let lp = TerminalListAutomationPeer(this, payload, writePtyBytes)
            currentListPeer <- Some lp
            this.RaiseAutomationEvent(AutomationEvents.StructureChanged)
        | "item" ->
            match currentListPeer with
            | Some lp -> lp.UpdateSelection(payload.SelectedIndex)
            | None ->
                // SelectionItem arrived without a preceding
                // SelectionShown — defensive skip. Per the
                // detector burst protocol, SelectionShown
                // always precedes SelectionItem; this branch
                // catches state drift only.
                ()
        | "dismissed" ->
            currentListPeer <- None
            this.RaiseAutomationEvent(AutomationEvents.StructureChanged)
        | _ ->
            // Unknown Kind — forward-compat: future selection
            // kinds (e.g. multi-select pickers) land here without
            // throwing.
            ()

    /// Cycle 46 PR-C — channel-side caret advance. Called from
    /// `Program.fs`'s boundary handler on tuple finalise (and
    /// from `speechCursorAnnounce` per PR-D) alongside the
    /// `TerminalView.Announce` call. Raises
    /// `AutomationEvents.TextPatternOnTextSelectionChanged` on
    /// this peer.
    ///
    /// The WPF enum name is verbose but precise: the
    /// `TextPatternOn*` prefix marks it as belonging to the
    /// UIA Text-pattern event family (the underlying UIA
    /// event ID is `TextPattern.TextSelectionChangedEvent`;
    /// WPF prefixes the enum name to keep it distinct from
    /// `LegacyIAccessibleSelectionChanged` etc.).
    ///
    /// Why the event is currently defensive rather than
    /// load-bearing: post-PR-D maintainer testing surfaced
    /// that NVDA does not react to a bare
    /// `TextPatternOnTextSelectionChanged` raise when
    /// `ITextProvider.GetSelection()` returns empty (which
    /// ours does — no selection model). The
    /// `Announce(text, activityId)` call is what NVDA
    /// actually reads; this event is kept as a defensive
    /// signal for review-cursor positioning and possible
    /// future use, and is paired with the channel-side
    /// `Announce` at every caller. See ADR 0002 §"Status
    /// notes" (2026-05-13 post-PR-D audit entry).
    ///
    /// Must be called on the WPF dispatcher thread (per the
    /// `AutomationPeer.RaiseAutomationEvent` contract). The
    /// existing boundary handler in `Program.fs` is already
    /// inside `window.Dispatcher.InvokeAsync` so the dispatch
    /// requirement is satisfied at the call site.
    ///
    /// See `docs/adr/0002-uia-textedit-caret-output.md`.
    member this.RaiseCaretMovedToTail() =
        this.RaiseAutomationEvent(
            AutomationEvents.TextPatternOnTextSelectionChanged)

    /// Add the Text pattern to this peer. For every other
    /// pattern interface we defer to the base implementation
    /// so the inherited behaviour (LegacyIAccessible, Window,
    /// etc. coming from `FrameworkElementAutomationPeer`) is
    /// preserved. Return type matches
    /// `AutomationPeer.GetPattern`'s annotation (`object?`) so
    /// F# 9 nullability accepts the base call's possibly-null
    /// return.
    ///
    /// The Text branch uses an explicit `obj | null` type
    /// annotation on a temporary binding so F# 9's nullability
    /// analysis widens `textProvider`'s declared
    /// `ITextProvider` (currently treated as non-nullable in
    /// the WPF reference assembly's annotation set) to the
    /// nullable return type of the override. Without the
    /// annotation, F# rejects both the bare upcast `:> obj`
    /// (FS3261, "this expression is nullable") and the
    /// pattern-match-on-null form (FS3261, "the type does
    /// not support null"). The annotation form sidesteps
    /// both: it's a widening assignment, not a narrowing
    /// pattern match.
    override _.GetPattern(patternInterface: PatternInterface) : obj | null =
        match patternInterface with
        | PatternInterface.Text ->
            // `textProvider` is `ContentHistoryTextProvider`
            // (see `ContentHistoryTextRange.fs`). Cycle 46
            // PR-B substrate-swapped this from the previous
            // screen-grid implementation; PR-D deleted the
            // legacy types. `RaiseCaretMovedToTail` (below) is
            // the channel-side counterpart that signals NVDA
            // when the tail has new content.
            let result : obj | null = textProvider
            result
        | _ -> base.GetPattern(patternInterface)
