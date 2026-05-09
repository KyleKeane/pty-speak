namespace Terminal.Core.Channels

open Terminal.Core

/// Translates portable `HotkeyRegistry` shapes into host-
/// specific gesture types.
///
/// **Why this lives in Terminal.Core:** the
/// `HotkeyRegistry.HotkeyKey` and `HotkeyRegistry.Modifier` DUs
/// (`HotkeyRegistry.fs`) are deliberately portable F# types so
/// substrate code can describe hotkey bindings without
/// importing host-specific input enums. Today's WPF host
/// translates these to `System.Windows.Input.KeyGesture` at
/// `Terminal.App/Program.fs:274-333`
/// (`translateHotkeyKey` + `translateHotkeyModifiers`); a
/// future Avalonia or GTK host would translate to its own
/// gesture type. This interface is the formal seam.
///
/// **Generic over `'TGesture`** so Terminal.Core does not
/// import any host-specific type. Callers pass the concrete
/// gesture type at the binding site:
///
/// ```fsharp
/// // In Terminal.App composition root (WPF-specific):
/// open System.Windows.Input
/// let wpfTranslator : IHotkeyTranslator<KeyGesture> =
///     { new IHotkeyTranslator<KeyGesture> with
///         member _.Translate(key, modifiers) =
///             let wpfKey = translateHotkeyKey key
///             let wpfMods = translateHotkeyModifiers modifiers
///             KeyGesture(wpfKey, wpfMods) }
/// ```
///
/// **Today's call sites (NOT cut over in Cycle 31b):**
/// `Terminal.App/Program.fs:274-333` continues to use the
/// existing `translateHotkeyKey` / `translateHotkeyModifiers`
/// helpers directly. Future migration (e.g., a
/// `KeyGestureTranslator.fs` adapter in Terminal.App that
/// implements this interface) is incremental.
///
/// **Why portable HotkeyKey vs WPF Key:** the codebase already
/// avoided WPF `Key` flowing into Terminal.Core (see the
/// existing `KeyEncoding.fs:11` doc-comment "Why a private DU
/// instead of WPF's `System.Windows.Input.Key`?"). This
/// interface formalises the existing separation; no new
/// portability work is required.
type IHotkeyTranslator<'TGesture> =
    /// Map a portable `HotkeyKey` + `Modifier` set into a host
    /// gesture descriptor. Implementations on Windows return a
    /// WPF `KeyGesture`; on Linux, the equivalent AT-SPI / GTK
    /// gesture descriptor.
    ///
    /// The empty `Set<Modifier>` (no modifiers) is valid input
    /// — implementations MUST handle it without throwing.
    abstract Translate:
        key: HotkeyRegistry.HotkeyKey *
        modifiers: Set<HotkeyRegistry.Modifier> ->
            'TGesture
