namespace Engine.Core

/// RELAUNCH-SPEC §5 (the locked data model) — the chunk
/// primitives. A conversation is a tree of typed chunks; an
/// agent response is decomposed on ingest into block-level
/// chunks (ADR 0011 E2 — the structure is already present in
/// the agent's markdown / structured stream and is kept, never
/// flattened: ADR 0008 at conversation granularity).
///
/// Identity (ADR 0011 E3): every chunk carries a durable opaque
/// id (GUID "N"), an immutable per-session capture sequence
/// (the temporal position), and a separate authored index (its
/// position among its parent's children). v1 only appends, but
/// the authored ordering exists from the first commit so the
/// §6.3 editor verbs are not a later model rewrite.
module Chunk =

    /// Durable opaque chunk identity. Never positional: reorder
    /// / branch / re-issue cannot invalidate an address.
    type ChunkId = ChunkId of string

    /// Allocate a fresh id. GUID "N" format — 32 lowercase hex
    /// chars, no braces/hyphens, safe in logs and file names.
    let newId () : ChunkId =
        ChunkId (System.Guid.NewGuid().ToString("N"))

    /// The typed chunk vocabulary (spec §5.1). Block-level
    /// markdown kinds (the E2 grain) plus the agent-interaction
    /// kinds the Claude stream provides directly. Only kinds a
    /// shipped phase produces are declared; cases are added by
    /// the phase that wires them (the CellEventBus discipline).
    type ChunkKind =
        /// A markdown heading; level 1–6.
        | Heading of level: int
        /// A markdown paragraph.
        | Paragraph
        /// A markdown list container; `ordered` distinguishes
        /// numbered from bulleted. Its `ListItem` chunks are its
        /// children in the tree, so "descend into a chunk's
        /// children" (spec §6.2) walks into the items.
        | ListBlock of ordered: bool
        /// One item of the parent `ListBlock`.
        | ListItem
        /// A fenced code block; the info-string language when
        /// the fence declared one.
        | CodeBlock of language: string option
        /// A markdown block quote.
        | BlockQuote
        /// A thematic break (horizontal rule). Carried as a
        /// chunk so narration can announce the boundary.
        | ThematicBreak
        /// The user's composed request (the §6.1 loop's input
        /// half; also the root of the branch its response chunks
        /// hang under).
        | UserRequest
        /// An agent tool invocation (typed in the stream).
        | ToolUse of toolName: string
        /// A tool's result; `isError` is the stream's own flag.
        | ToolResult of isError: bool
        /// An agent-reported error (e.g. a failed turn).
        | AgentError
        /// An engine-side note (lifecycle, participant info).
        | SystemNote

    /// The atomic node (spec §5.1). Immutable record; the tree
    /// (ChunkTree.fs) owns structure and ordering indices.
    type Chunk =
        { Id: ChunkId
          Kind: ChunkKind
          /// The chunk's text content, rendered for narration
          /// and navigation. For container kinds (ListBlock)
          /// this may be a short synthesized label; leaves carry
          /// their literal text.
          Text: string
          /// Immutable temporal position within the session —
          /// assigned by the tree at append, never reused.
          CaptureSeq: int
          /// Position among the parent's children in authored
          /// order. v1 append-only, so it equals the insertion
          /// index; editor verbs (§6.3) will reorder it later.
          AuthoredIndex: int
          /// The parent chunk — `None` for top-level chunks of
          /// the main thread. A clarification branch's
          /// `UserRequest` has the anchor chunk as its parent
          /// (spec §5.1 "child branch anchored to the chunk it
          /// is about").
          Parent: ChunkId option }
