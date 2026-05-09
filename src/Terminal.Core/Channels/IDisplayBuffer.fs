namespace Terminal.Core.Channels

open Terminal.Core

/// Read-side abstraction for any host renderer that needs a
/// snapshot of the substrate's screen-grid state.
///
/// **Why this lives in Terminal.Core:** the screen grid (today's
/// `Terminal.Core.Screen`) is substrate-side per
/// `docs/CORE-ABSTRACTION-BOUNDARY.md` §2. Host renderers
/// (today's WPF `TerminalView`, future Avalonia /
/// GTK / AppKit hosts) need a snapshot of cells + cursor
/// position to draw a frame; this interface is the formal
/// boundary they consume.
///
/// **Substrate dichotomy reminder:** `IDisplayBuffer` is the
/// snapshot-of-grid surface. Linear-text-stream consumers
/// (Cycle 34 onwards) get a separate substrate surface (the
/// `LinearTextStream` producer's high-water-mark commits) —
/// DO NOT retrofit linear streams into `IDisplayBuffer`. The
/// substrate dichotomy keeps them separate per the boundary
/// doc; `IDisplayBuffer` stays focused on the grid (alt-screen
/// TUIs + the visual rendering surface).
///
/// **Today's call sites (NOT cut over in Cycle 31b):** seven
/// direct `Screen.SnapshotRows` calls remain on the existing
/// type:
///
/// - `Views/TerminalView.cs:1002` — UI render (Cycle 32b
///   cutover target).
/// - `Terminal.App/Program.fs:1258` — `handleAltScreenToggled`.
/// - `Terminal.App/Program.fs:1345` — `handleRowsChanged`.
/// - `Terminal.App/Program.fs:1375` — `handleTick`.
/// - `Terminal.App/Program.fs:1480` — `handleSelectionChanged`.
/// - `Terminal.App/Program.fs:1534` — `reportActivityIfQuiet`.
/// - `Terminal.App/Program.fs:2574` — `handleNvdaAnnounce`.
/// - `Terminal.Accessibility/TerminalAutomationPeer.fs:742` —
///   UIA text-range queries.
///
/// Cycle 32b ships a `DefaultDisplayBuffer` adapter (in
/// Terminal.App composition root) wrapping the existing
/// `Screen` instance, and migrates `TerminalView.cs:1002` to
/// consume it. The other call sites stay direct until a future
/// cycle has concrete value motivation.
///
/// **Locking contract:** implementations MUST acquire any
/// internal state lock during snapshot to prevent torn reads.
/// The reference implementation (`Screen.SnapshotRows` at
/// `Screen.fs:541-553`) uses `lock gate` around the cell
/// `Array.init` + cursor capture; new implementations MUST
/// preserve atomic cell + cursor capture so consumers don't
/// see a cursor from frame N+1 paired with cells from frame
/// N.
///
/// **Cell type portability:** the returned `Cell[][]` is fully
/// OS-portable — composed of `Rune` (UTF-32 scalar),
/// `ColorSpec` (platform-neutral), and `SgrAttrs` (terminal
/// attributes). No WPF / GTK / AppKit concerns leak into the
/// substrate.
type IDisplayBuffer =
    /// Snapshot a contiguous row range. Returns:
    ///
    /// - `sequenceNumber` (`int64`): monotonic; consumers compare
    ///   for change detection between snapshots.
    /// - `cursor` (`int * int`): cursor `(row, col)` position
    ///   captured atomically with the cell snapshot.
    /// - `rows` (`Cell[][]`): jagged 2D array where outer length
    ///   equals `count` and inner length equals the substrate's
    ///   column count.
    ///
    /// `startRow` is 0-indexed from the top of the active buffer;
    /// `count` MUST be ≤ the buffer's row count. Implementations
    /// SHOULD validate the range and throw `ArgumentOutOfRangeException`
    /// on overflow rather than silently truncate (matches the
    /// existing `Screen.SnapshotRows:541-553` validation).
    abstract Snapshot:
        startRow: int *
        count: int ->
            int64 * (int * int) * Cell[][]
