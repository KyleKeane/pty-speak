module PtySpeak.Tests.Unit.TerminalListAutomationPeerTests

open System
open System.Windows.Automation
open System.Windows.Automation.Peers
open System.Windows.Automation.Provider
open Xunit
open Terminal.Core
open Terminal.Accessibility

/// Cycle 37b — pins the TerminalListAutomationPeer +
/// TerminalListItemAutomationPeer contract:
///
///   * List peer reports `ControlType.List`; ListItem peer
///     reports `ControlType.ListItem`.
///   * `GetChildren()` returns N items matching `ItemCount`.
///   * `ISelectionProvider`: `CanSelectMultiple = false`,
///     `IsSelectionRequired = true`.
///   * `UpdateSelection(newIdx)` mutates parent's
///     `SelectedIndex`; ListItem.`IsSelected` reflects parent
///     state.
///   * `ListItem.GetPositionInSet` / `GetSizeOfSet` populated.
///   * `ListItem.GetPattern(SelectionItem | Invoke)` returns
///     non-null pattern providers.
///   * `IInvokeProvider.Invoke()` writes `\r` (0x0D) to the
///     PTY via the supplied `writePtyBytes` callback.
///
/// **What these tests do NOT cover** (manual NVDA matrix in
/// `docs/ACCESSIBILITY-TESTING.md` Cycle 37 covers it):
///
///   * UIA event raising visible to a live UIA client. Tests
///     run outside the WPF host so `RaiseAutomationEvent` is a
///     no-op (it doesn't throw — that's the relevant
///     invariant for these unit tests).
///   * `ProviderFromPeer` returns null outside the WPF tree;
///     `ISelectionProvider.GetSelection()` is exercised at the
///     "doesn't throw, returns array of length 0 or 1" level
///     but the array's contents (the wrapped peer) aren't
///     asserted.
///   * `TerminalAutomationPeer.UpdateSelectionState` end-to-end
///     transitions — that needs a real `FrameworkElement`
///     parent, which requires a WPF runtime context (covered
///     by manual NVDA matrix instead).

// ---------------------------------------------------------------------
// Test stub: minimal AutomationPeer that satisfies F#'s abstract-
// override checklist for use as a parent in TerminalListAutomationPeer's
// constructor. Outside the WPF tree, most peer methods are never
// invoked anyway — this stub exists to satisfy the type signature.
// ---------------------------------------------------------------------

type private TestStubAutomationPeer() =
    inherit AutomationPeer()

    override _.GetAutomationControlTypeCore() = AutomationControlType.Custom
    override _.GetClassNameCore() = "TestStubPeer"
    override _.GetNameCore() = "test-stub"
    override _.GetBoundingRectangleCore() = System.Windows.Rect.Empty
    override _.GetClickablePointCore() = System.Windows.Point()
    override _.GetChildrenCore() = System.Collections.Generic.List<AutomationPeer>()
    override _.GetPattern(_) : obj | null = null
    override _.HasKeyboardFocusCore() = false
    override _.IsContentElementCore() = true
    override _.IsControlElementCore() = true
    override _.IsEnabledCore() = true
    override _.IsKeyboardFocusableCore() = false
    override _.IsOffscreenCore() = false
    override _.IsPasswordCore() = false
    override _.IsRequiredForFormCore() = false
    override _.SetFocusCore() = ()

// ---------------------------------------------------------------------
// Fixture builders
// ---------------------------------------------------------------------

let private shownPayload (items: string[]) (selectedIdx: int) : SelectionRawPayload =
    { Kind = "shown"
      ItemCount = items.Length
      SelectedIndex = selectedIdx
      ItemIndex = -1
      AllItems = items
      ItemText = "" }

let private noOpWrite : Action<byte[]> = Action<byte[]>(fun _ -> ())

let private makeListPeer
        (items: string[])
        (selectedIdx: int)
        (write: Action<byte[]>)
        : TerminalListAutomationPeer =
    let parent = TestStubAutomationPeer() :> AutomationPeer
    let payload = shownPayload items selectedIdx
    TerminalListAutomationPeer(parent, payload, write)

// ---------------------------------------------------------------------
// TerminalListAutomationPeer
// ---------------------------------------------------------------------

[<Fact>]
let ``TerminalListAutomationPeer reports ControlType.List`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "No" |] 0 noOpWrite
    Assert.Equal(
        AutomationControlType.List,
        (peer :> AutomationPeer).GetAutomationControlType())

[<Fact>]
let ``TerminalListAutomationPeer reports ClassName "TerminalList"`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "No" |] 0 noOpWrite
    Assert.Equal(
        "TerminalList",
        (peer :> AutomationPeer).GetClassName())

[<Fact>]
let ``TerminalListAutomationPeer reports Name "Selection prompt"`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "No" |] 0 noOpWrite
    Assert.Equal(
        "Selection prompt",
        (peer :> AutomationPeer).GetName())

[<Fact>]
let ``GetChildren returns N items matching ItemCount`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "Always"; "No" |] 1 noOpWrite
    let children = (peer :> AutomationPeer).GetChildren()
    Assert.NotNull(children)
    Assert.Equal(4, children.Count)

[<Fact>]
let ``ISelectionProvider reports CanSelectMultiple=false and IsSelectionRequired=true`` () =
    let peer = makeListPeer [| "Edit"; "Yes" |] 0 noOpWrite
    let provider = peer :> ISelectionProvider
    Assert.False(provider.CanSelectMultiple)
    Assert.True(provider.IsSelectionRequired)

[<Fact>]
let ``ISelectionProvider.GetSelection returns array of length 1 when selected index is in range`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "Always"; "No" |] 2 noOpWrite
    let provider = peer :> ISelectionProvider
    let selection = provider.GetSelection()
    // ProviderFromPeer returns null outside WPF tree, so the
    // array entry may be null — what we pin here is the LENGTH
    // (1 means "we found the selected item").
    Assert.Equal(1, selection.Length)

[<Fact>]
let ``ISelectionProvider.GetSelection returns empty when SelectedIndex is out of range`` () =
    let peer = makeListPeer [| "Edit"; "Yes" |] -1 noOpWrite
    let provider = peer :> ISelectionProvider
    let selection = provider.GetSelection()
    Assert.Equal(0, selection.Length)

[<Fact>]
let ``UpdateSelection mutates SelectedIndex and does not throw`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "Always"; "No" |] 0 noOpWrite
    Assert.Equal(0, peer.SelectedIndex)
    peer.UpdateSelection(2)
    Assert.Equal(2, peer.SelectedIndex)

[<Fact>]
let ``GetPattern(Selection) returns the ISelectionProvider; other patterns return null`` () =
    let peer = makeListPeer [| "Edit"; "Yes" |] 0 noOpWrite
    let basePeer = peer :> AutomationPeer
    Assert.NotNull(basePeer.GetPattern(PatternInterface.Selection))
    Assert.Null(basePeer.GetPattern(PatternInterface.Invoke))
    Assert.Null(basePeer.GetPattern(PatternInterface.Text))

// ---------------------------------------------------------------------
// TerminalListItemAutomationPeer
// ---------------------------------------------------------------------

[<Fact>]
let ``TerminalListItemAutomationPeer reports ControlType.ListItem`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "No" |] 0 noOpWrite
    let children = (peer :> AutomationPeer).GetChildren()
    let firstChild = children.[0]
    Assert.Equal(
        AutomationControlType.ListItem,
        firstChild.GetAutomationControlType())

[<Fact>]
let ``ListItem reports correct GetPositionInSet and GetSizeOfSet`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "Always"; "No" |] 1 noOpWrite
    let children = (peer :> AutomationPeer).GetChildren()
    Assert.Equal(1, children.[0].GetPositionInSet())
    Assert.Equal(2, children.[1].GetPositionInSet())
    Assert.Equal(4, children.[0].GetSizeOfSet())
    Assert.Equal(4, children.[3].GetSizeOfSet())

[<Fact>]
let ``ListItem.IsSelected reflects parent.SelectedIndex`` () =
    let peer = makeListPeer [| "Edit"; "Yes"; "Always"; "No" |] 1 noOpWrite
    let children = (peer :> AutomationPeer).GetChildren()
    let getIsSelected (i: int) =
        let item = children.[i] :?> TerminalListItemAutomationPeer
        (item :> ISelectionItemProvider).IsSelected
    // Parent's SelectedIndex = 1; only ListItem at index 1 is selected.
    Assert.False(getIsSelected 0)
    Assert.True(getIsSelected 1)
    Assert.False(getIsSelected 2)
    Assert.False(getIsSelected 3)
    // Mutate selection; ListItem state follows.
    peer.UpdateSelection(3)
    Assert.False(getIsSelected 0)
    Assert.False(getIsSelected 1)
    Assert.False(getIsSelected 2)
    Assert.True(getIsSelected 3)

[<Fact>]
let ``ListItem GetPattern(SelectionItem) returns ISelectionItemProvider`` () =
    let peer = makeListPeer [| "Edit"; "Yes" |] 0 noOpWrite
    let firstChild = (peer :> AutomationPeer).GetChildren().[0]
    let pattern = firstChild.GetPattern(PatternInterface.SelectionItem)
    Assert.NotNull(pattern)
    Assert.IsAssignableFrom<ISelectionItemProvider>(pattern) |> ignore

[<Fact>]
let ``ListItem GetPattern(Invoke) returns IInvokeProvider`` () =
    let peer = makeListPeer [| "Edit"; "Yes" |] 0 noOpWrite
    let firstChild = (peer :> AutomationPeer).GetChildren().[0]
    let pattern = firstChild.GetPattern(PatternInterface.Invoke)
    Assert.NotNull(pattern)
    Assert.IsAssignableFrom<IInvokeProvider>(pattern) |> ignore

[<Fact>]
let ``ListItem.Invoke writes 0x0D (Enter) to the PTY via writePtyBytes`` () =
    let captured = ResizeArray<byte[]>()
    let recorder = Action<byte[]>(fun bytes -> captured.Add(bytes))
    let peer = makeListPeer [| "Edit"; "Yes" |] 0 recorder
    let firstChild = (peer :> AutomationPeer).GetChildren().[0]
    let invoke = firstChild.GetPattern(PatternInterface.Invoke) :?> IInvokeProvider
    invoke.Invoke()
    Assert.Equal(1, captured.Count)
    Assert.Equal<byte[]>([| 0x0Duy |], captured.[0])
