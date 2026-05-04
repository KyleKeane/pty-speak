namespace Terminal.Core

/// Stage 8d.1 — WASAPI Earcons channel. Translates
/// `RenderEarcon earconId` instructions into a `play` callback
/// invocation; the composition root in Program.fs binds the
/// callback to `Terminal.Audio.EarconPlayer.play
/// EarconPalette.defaultPalette` (which feeds NAudio's
/// WasapiOut). Other RenderInstruction cases (RenderText /
/// RenderText2 / RenderRaw) are skipped — those go to the NVDA
/// or FileLogger channels, not earcons.
///
/// The marshal-callback pattern keeps `Terminal.Core` free of
/// NAudio dependency (same as NvdaChannel keeps Terminal.Core
/// free of WPF, and FileLoggerChannel takes an injected ILogger
/// instead of constructing one). The composition root threads
/// the platform binding through.
///
/// **Mute state.** Module-level mutable bool. The Ctrl+Shift+M
/// hotkey handler in Program.fs calls `toggle ()` to flip
/// state; the channel's Send checks `isMuted ()` before
/// invoking `play`. Process-wide because the user expects the
/// mute toggle to affect all subsequent earcons regardless of
/// shell or session. Single-thread-init pattern matches the
/// other registries; cross-thread access on a `bool` is safe
/// under the standard .NET memory model.
///
/// **Spec reference.** `spec/event-and-output-framework.md`
/// Part B.4.2 (WASAPI Earcons channel) + Part C.1 (8d row).
/// Per-shell mute state via TOML
/// (`[shell.X] muteEarcons = true`) is a 9c TOML loader concern,
/// out of scope for 8d.1.
module EarconChannel =

    /// Stable channel identifier registered with the
    /// dispatcher's `ChannelRegistry`. Profiles refer to this
    /// string in their `ChannelDecision.Channel` field.
    [<Literal>]
    let id: ChannelId = "earcon"

    let private muteLock : obj = obj ()
    let mutable private muted : bool = false

    /// Flip the mute state. Returns the new state. Called by
    /// the Ctrl+Shift+M hotkey handler in Program.fs.
    let toggle () : bool =
        lock muteLock (fun () ->
            muted <- not muted
            muted)

    /// Read the current mute state. The channel's Send checks
    /// this before invoking `play` so the play callback isn't
    /// called when muted.
    let isMuted () : bool =
        muted

    /// Test-only — reset mute to false. Tests use this in
    /// their setup so test runs don't leak mute state across
    /// each other. Production composition root never calls
    /// this.
    let internal clearForTests () : unit =
        lock muteLock (fun () -> muted <- false)

    /// Construct a Channel that plays earcons via the supplied
    /// callback. The callback receives the earcon-id string
    /// and is responsible for the actual audio output (in
    /// production: `Terminal.Audio.EarconPlayer.play
    /// defaultPalette`; in tests: a recording function that
    /// captures invocations).
    let create (play: string -> unit) : Channel =
        { Id = id
          Send =
            fun _event render ->
                if isMuted () then
                    ()
                else
                    match render with
                    | RenderEarcon earconId -> play earconId
                    | RenderText _ -> ()
                    | RenderText2 (_, _) -> ()
                    | RenderRaw _ -> () }
