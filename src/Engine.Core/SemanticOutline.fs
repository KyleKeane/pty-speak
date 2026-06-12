namespace Engine.Core

open Engine.Core.MarkdownChunker

/// ADR 0012 S1 — the heading-scoped semantic outline. The
/// chunker yields the model's block structure as a flat list
/// (headings are siblings of the prose they head); this module
/// recovers the *section* structure that flat shape throws
/// away: a heading absorbs every following spec — including
/// deeper headings — as children, until the next heading of
/// equal or shallower level closes its scope.
///
/// ADR 0008 discipline: this is recovery of structure the
/// source unambiguously provides (the author chose the heading
/// levels), not inference. Content before the first heading
/// stays top-level — the source put it there.
///
/// Pure: spec forest in, spec forest out. Ingest applies it
/// between decomposition and append, so the chunk tree carries
/// the outline and the §6.2 verbs get real hierarchy: `next`
/// at the top level is section-to-section; `descend` enters a
/// section.
module SemanticOutline =

    /// Collect specs into `bound`-scoped children: stop (without
    /// consuming) at any heading of level <= bound. Returns the
    /// collected siblings + the unconsumed rest.
    let rec private collect
            (bound: int)
            (specs: ChunkSpec list)
            : ChunkSpec list * ChunkSpec list =
        match specs with
        | [] -> [], []
        | spec :: rest ->
            match spec.Kind with
            | Chunk.Heading level when level <= bound ->
                // This heading closes the current scope; the
                // caller owns it.
                [], specs
            | Chunk.Heading level ->
                // A deeper heading: it opens a nested section
                // absorbing its own scope first.
                let inner, afterInner = collect level rest
                let section =
                    { spec with Children = spec.Children @ inner }
                let siblings, afterSiblings = collect bound afterInner
                (section :: siblings), afterSiblings
            | _ ->
                // Ordinary block: a sibling at this scope. Its
                // own children (list items etc.) are untouched.
                let siblings, after = collect bound rest
                (spec :: siblings), after

    /// Nest a flat block forest into its heading-scoped outline.
    /// Bound 0 means no enclosing heading: every real heading
    /// (level >= 1) opens a section.
    let nest (specs: ChunkSpec list) : ChunkSpec list =
        collect 0 specs |> fst
