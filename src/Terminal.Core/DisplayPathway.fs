namespace Terminal.Core

open System

/// Phase A — Layer 3 display-pathway interface.
///
/// A `DisplayPathway` consumes the canonical-state substrate
/// (Layer 2) and emits OutputEvents that flow through the
/// existing Profile + Channel + Dispatcher substrate
/// (Layer 4 / Stages 8a-8d). Each shell selects ONE active
/// pathway; the PathwayPump task in Program.fs drives the
/// per-frame `Consume` calls.
///
/// **Key contrast with `Profile` (`OutputEventTypes.fs`):**
/// - Profiles consume OutputEvents and produce ChannelDecisions
///   (HOW to render an event).
/// - Pathways consume canonical state and produce OutputEvents
///   (HOW to translate screen state into events).
/// - Two distinct abstractions; Phase A introduces the pathway
///   layer above the existing profile layer.
///
/// **Pathway lifecycle.** A pathway has four entry points:
/// - `Consume`: called for each new canonical-state update
///   (typically when a `ScreenNotification.RowsChanged` arrives).
/// - `Tick`: called periodically (the PathwayTickPump runs a
///   `PeriodicTimer(50ms)` and calls Tick on each tick) so
///   pathways with trailing-edge flush behaviour (StreamPathway
///   debounce) can release pending events when no new
///   canonical state has arrived.
/// - `OnModeBarrier`: called when a `ScreenNotification.ModeChanged`
///   arrives. The pathway flushes any pending state (so the
///   user hears pre-mode-change content) and resets its
///   baselines so the post-mode-change first emit treats every
///   row as new.
/// - `Reset`: called when the active shell switches. The
///   pathway clears its state so the new shell's session
///   starts with a clean baseline.
///
/// **Empty-array contract.** Returning `[||]` from any of the
/// emit methods means "no events to dispatch" — same semantics
/// as the existing Profile.Apply empty-array contract.
///
/// **Spec reference.** The architectural-spec draft at the top
/// of `/root/.claude/plans/hello-i-lost-my-velvet-deer.md`
/// (the design proposal the maintainer approved 2026-05-05).
[<RequireQualifiedAccess>]
module DisplayPathway =

    type T =
        { /// Stable identifier (e.g. "stream", "tui-review",
          /// "claude-code"). Used for diagnostics + the eventual
          /// Phase B TOML config (`[shell.X] pathway = "..."`).
          Id: string
          /// Called for each new canonical-state snapshot the
          /// pathway should consume. Returns the OutputEvents
          /// to dispatch through the Profile + Channel layer.
          Consume: CanonicalState.Canonical -> OutputEvent[]
          /// Called periodically by the PathwayTickPump (50ms
          /// cadence). Pathways with trailing-edge flush
          /// (StreamPathway debounce) emit pending events here
          /// when no new canonical state has arrived. Most
          /// pathways return `[||]`.
          Tick: DateTimeOffset -> OutputEvent[]
          /// Called when a `ScreenNotification.ModeChanged`
          /// arrives. The pathway flushes any pending state
          /// (so the user hears pre-mode-change content) and
          /// resets baselines. Returns the flushed events to
          /// dispatch BEFORE the barrier OutputEvent itself.
          OnModeBarrier: DateTimeOffset -> OutputEvent[]
          /// Called when the active shell switches. The pathway
          /// resets its internal state so the new shell's
          /// session starts with a clean baseline (no leaked
          /// row hashes, no pending debounce, etc.).
          Reset: unit -> unit
          /// Called immediately after `Reset` on a shell-switch
          /// — seeds the pathway's diff baseline with the
          /// supplied canonical state. Without this seed, the
          /// next `Consume` after a `Reset` treats every row as
          /// "new" and emits the entire screen verbatim — which
          /// surfaces as NVDA reading the previous shell's
          /// stale screen content after `Ctrl+Shift+1/2/3`
          /// hot-switch (the screen buffer isn't cleared on
          /// switch; only the new shell's paint progressively
          /// overwrites the old content). Seeding the baseline
          /// against the screen's snapshot at the moment of
          /// switch makes subsequent `Consume` emits diff-only
          /// against the post-switch frame.
          ///
          /// **No emission.** `SetBaseline` MUST NOT emit
          /// OutputEvents — it only updates internal hash state.
          /// Pathways without baseline state (TuiPathway) treat
          /// it as a no-op.
          SetBaseline: CanonicalState.Canonical -> unit }
