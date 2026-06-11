namespace Engine.Core

/// RELAUNCH-SPEC §4.3 / ADR 0011 E4 — the typed vocabulary a
/// participant's structured stream is normalized into (the
/// §0.2 event handler's output for the agent-result input
/// class). The Claude Code CLI's stream-json interface is the
/// first producer (`ClaudeStreamJson.fs`); further participants
/// (Aider, Wolfram, …) normalize into the same vocabulary.
///
/// ADR 0008 discipline: recover exactly the structure the
/// stream unambiguously provides; anything outside the known
/// vocabulary is surfaced as a typed `Unknown` / `ParseError`
/// carrying its provenance — never silently dropped, never
/// relayed as ambiguous text.
module AgentEvent =

    /// One content block of an assistant message, as typed by
    /// the stream itself.
    type ContentBlock =
        /// Prose (markdown) — decomposed into chunks on ingest.
        | Text of text: string
        /// A tool invocation; `inputJson` is the raw JSON of
        /// the tool input (kept verbatim — the §6.2 "semantic
        /// info about run code" verb renders it on demand).
        | ToolUse of id: string * name: string * inputJson: string
        /// A block type outside the known vocabulary; the type
        /// tag is preserved for diagnostics.
        | UnknownBlock of blockType: string

    /// A tool's result, echoed back through the stream.
    type ToolResult =
        { ToolUseId: string
          /// Flattened text of the result content (string or
          /// text-block-array in the wire format).
          Content: string
          IsError: bool }

    /// One typed event per stream-json line.
    type AgentEvent =
        /// `system/init` — the session began; carries the CLI
        /// session id used for `--resume` continuity (E4).
        | SessionInit of sessionId: string * model: string option
        /// A complete assistant message (the E5 seal boundary).
        | AssistantMessage of blocks: ContentBlock list
        /// Tool results re-entering the conversation.
        | ToolResults of results: ToolResult list
        /// The turn finished; `resultText` is the CLI's final
        /// result field when present.
        | TurnResult of
            isError: bool *
            resultText: string option *
            sessionId: string option
        /// A line whose `type` is outside the known vocabulary
        /// — surfaced, not dropped (ADR 0008).
        | Unknown of eventType: string * rawJson: string
        /// A line that was not valid JSON (or had no `type`).
        | ParseError of message: string * rawLine: string
