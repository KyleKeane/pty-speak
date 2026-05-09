module PtySpeak.Tests.Unit.AppReservedHotkeysMirrorTests

open Xunit
open System.Windows.Input
open Terminal.Core
open PtySpeak.Views

// ---------------------------------------------------------------------
// Cycle 26b — F# / C# mirror parity invariant
// ---------------------------------------------------------------------
//
// `HotkeyRegistry.builtIns` (F#, `Terminal.Core/HotkeyRegistry.fs`)
// is the canonical registry of every app-reserved hotkey.
// `TerminalView.AppReservedHotkeys` (C#, `src/Views/TerminalView.cs`)
// is the per-keystroke hot-path mirror consulted by
// `OnPreviewKeyDown` to decide whether to mark `e.Handled = true`
// (so the parent Window's `InputBindings` machinery can fire the
// `RoutedCommand`).
//
// The two surfaces are intentionally split — the C# array avoids
// F#/C# interop cost on every keystroke — but they MUST stay in
// sync. Before Cycle 26b this was maintained by maintainer
// convention only ("update both surfaces in the same PR"); a
// missed update would cause a hotkey to flow through to the
// shell as plain bytes instead of firing its handler.
//
// This fixture pins the invariant at test time. Every
// gesture-bearing entry in `HotkeyRegistry.builtIns` (i.e. those
// with `Some Key` and `Some Modifiers`; menu-only commands are
// excluded by definition since they have no keyboard gesture)
// must have a corresponding row in `AppReservedHotkeys`. The
// reverse is also pinned: every C# row corresponds to some F#
// entry.
//
// Both surfaces are accessible to the test:
//   - `TerminalView` is `public` and `Views.csproj` declares
//     `<InternalsVisibleTo Include="PtySpeak.Tests.Unit" />`.
//   - `AppReservedHotkeys` is `public static readonly`.
//
// The test inlines its own translation from F# `HotkeyKey` to
// WPF `Key` and from F# `Modifier` to WPF `ModifierKeys`. The
// production translation lives in `Program.fs`'s `private`
// `translateHotkeyKey` / `translateHotkeyModifiers`. The
// duplication is intentional: a divergence between either
// translation surfaces immediately as a test failure rather
// than at NVDA-test time.

let private translateKey (k: HotkeyRegistry.HotkeyKey) : Key =
    match k with
    | HotkeyRegistry.Letter c ->
        match System.Char.ToUpperInvariant(c) with
        | 'A' -> Key.A | 'B' -> Key.B | 'C' -> Key.C | 'D' -> Key.D
        | 'E' -> Key.E | 'F' -> Key.F | 'G' -> Key.G | 'H' -> Key.H
        | 'I' -> Key.I | 'J' -> Key.J | 'K' -> Key.K | 'L' -> Key.L
        | 'M' -> Key.M | 'N' -> Key.N | 'O' -> Key.O | 'P' -> Key.P
        | 'Q' -> Key.Q | 'R' -> Key.R | 'S' -> Key.S | 'T' -> Key.T
        | 'U' -> Key.U | 'V' -> Key.V | 'W' -> Key.W | 'X' -> Key.X
        | 'Y' -> Key.Y | 'Z' -> Key.Z
        | other -> failwithf "translateKey: unmapped letter %c" other
    | HotkeyRegistry.Digit n ->
        match n with
        | 1 -> Key.D1 | 2 -> Key.D2 | 3 -> Key.D3
        | 4 -> Key.D4 | 5 -> Key.D5 | 6 -> Key.D6
        | 7 -> Key.D7 | 8 -> Key.D8 | 9 -> Key.D9
        | other -> failwithf "translateKey: unmapped digit %d" other
    | HotkeyRegistry.Semicolon -> Key.OemSemicolon

let private translateMods (mods: Set<HotkeyRegistry.Modifier>) : ModifierKeys =
    let mutable result = ModifierKeys.None
    for m in mods do
        match m with
        | HotkeyRegistry.Ctrl -> result <- result ||| ModifierKeys.Control
        | HotkeyRegistry.Shift -> result <- result ||| ModifierKeys.Shift
        | HotkeyRegistry.Alt -> result <- result ||| ModifierKeys.Alt
    result

/// All gesture-bearing F# entries translated to WPF (Key, ModifierKeys).
let private fSourceGestures () : Set<Key * ModifierKeys> =
    HotkeyRegistry.builtIns
    |> List.choose (fun h ->
        match h.Key, h.Modifiers with
        | Some k, Some m -> Some (translateKey k, translateMods m)
        | _ -> None)
    |> Set.ofList

/// All C# AppReservedHotkeys rows as (Key, ModifierKeys) tuples.
/// The C# array element type is the value tuple
/// `(Key Key, ModifierKeys Modifiers, string Description)`,
/// which the F# pattern `struct (k, m, _)` destructures cleanly.
let private cMirrorGestures () : Set<Key * ModifierKeys> =
    TerminalView.AppReservedHotkeys
    |> Array.map (fun struct (k, m, _) -> (k, m))
    |> Set.ofArray

[<Fact>]
let ``every gesture-bearing AppCommand has a matching AppReservedHotkeys row`` () =
    let fSet = fSourceGestures ()
    let cSet = cMirrorGestures ()
    let missing = Set.difference fSet cSet
    Assert.True(
        Set.isEmpty missing,
        sprintf
            "F# HotkeyRegistry has gestures with no C# AppReservedHotkeys row: %A. \
             Add the matching (Key, ModifierKeys, Description) tuple to \
             TerminalView.AppReservedHotkeys."
            missing)

[<Fact>]
let ``every AppReservedHotkeys row has a matching gesture-bearing AppCommand`` () =
    let fSet = fSourceGestures ()
    let cSet = cMirrorGestures ()
    let orphan = Set.difference cSet fSet
    Assert.True(
        Set.isEmpty orphan,
        sprintf
            "C# TerminalView.AppReservedHotkeys has rows with no F# HotkeyRegistry \
             entry: %A. Either add a matching AppCommand (DU + builtIns row) or \
             remove the orphan from AppReservedHotkeys."
            orphan)

[<Fact>]
let ``F# gesture-bearing count matches C# AppReservedHotkeys count`` () =
    // Belt-and-suspenders: the two set-difference tests above
    // catch any asymmetric mismatch, but a same-cardinality
    // assertion makes the failure mode "off by one" obvious in
    // a CI log.
    let fCount = (fSourceGestures ()).Count
    let cCount = (cMirrorGestures ()).Count
    Assert.Equal(fCount, cCount)
