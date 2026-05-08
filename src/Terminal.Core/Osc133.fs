namespace Terminal.Core

open System
open System.Text

/// Tier 1.B — OSC 133 escape-sequence parser.
///
/// **OSC 133** ("FinalTerm-style shell integration") is the
/// shell-emitted protocol for marking prompt boundaries. The
/// shell wraps prompt + command + output regions in escape
/// sequences:
///
/// - `ESC ] 133 ; A BEL` — PromptStart (a new prompt is about
///   to be drawn)
/// - `ESC ] 133 ; B BEL` — CommandStart (user pressed Enter)
/// - `ESC ] 133 ; C BEL` — OutputStart (command output begins)
/// - `ESC ] 133 ; D BEL` — CommandFinished (no exit code)
/// - `ESC ] 133 ; D ; <int> BEL` — CommandFinished with code
/// - Optional `aid=<id>` and other `key=value` parameters
///   after the kind discriminator.
///
/// **Parser context**: the upstream VT500 parser
/// (`Terminal.Parser.Parser`) emits `OscDispatch(parms,
/// terminator)` events where `parms: byte[][]` is split on
/// `;`. The parser caps total payload at `MAX_OSC_RAW = 1024`
/// bytes — this module inherits that bound.
///
/// **Module scope**: pure parsing logic. Returns `Option`
/// rather than throwing; malformed inputs return `None`
/// (silently dropped, matching the Screen.Apply silent-drop
/// policy for unknown OSC types). Consumers (Screen.Apply +
/// future Tier 1.C heuristic fallback) call `tryParse` then
/// `Option.iter` to publish the resulting boundary.
///
/// **Why a separate module from Screen.fs**: testable in
/// isolation. Unit tests feed `byte[][]` directly to
/// `tryParse`; no Screen needed. Tier 1.C's heuristic
/// fallback module reuses parser logic for the cases where
/// it can recover OSC 133-shaped data from non-OSC-emitting
/// shells.
///
/// **Security**: OSC 133 is metadata only — no clipboard
/// access, no filesystem writes, no execution surface. The
/// upstream `MAX_OSC_RAW = 1024` cap bounds attacker-
/// controlled `aid=` values. SessionModel's bounded ring
/// buffer (default 100 tuples) bounds spam. No threat
/// surface beyond "adversarial shell can produce
/// false-positive prompt boundaries", which the
/// `BoundarySource` field exposes for downstream confidence
/// logic.
[<RequireQualifiedAccess>]
module Osc133 =

    /// Decode an OSC parameter byte array as ASCII. OSC 133
    /// values are ASCII-only by spec; non-ASCII bytes get
    /// mapped to `?` per the encoding's fallback. Total
    /// length is bounded by the parser's MAX_OSC_RAW cap.
    let private decodeAscii (bytes: byte[]) : string =
        Encoding.ASCII.GetString(bytes)

    /// Split a `key=value` parameter on the first `=` byte.
    /// Returns `None` if no `=` is present (malformed
    /// parameter; silently dropped). Returns `Some (key,
    /// value)` otherwise; both halves are decoded ASCII.
    let private trySplitKeyValue (bytes: byte[]) : (string * string) option =
        let eqIdx = Array.tryFindIndex (fun b -> b = byte '=') bytes
        match eqIdx with
        | None -> None
        | Some idx ->
            let keyBytes = Array.sub bytes 0 idx
            let valueBytes = Array.sub bytes (idx + 1) (bytes.Length - idx - 1)
            Some (decodeAscii keyBytes, decodeAscii valueBytes)

    /// Parse the kind discriminator (parms.[1]). Returns
    /// `None` for unknown letters or empty bytes. The exit-
    /// code parameter for kind D is consumed at the next
    /// param index; this function returns the kind +
    /// "consumes-extra-param" flag so the caller knows where
    /// key=value parsing starts.
    let private parseKind
            (kindBytes: byte[])
            (parms: byte[][])
            : (BoundaryKind * int) option
            =
        if kindBytes.Length <> 1 then None
        else
            match kindBytes.[0] with
            | 0x41uy (* 'A' *) -> Some (BoundaryKind.PromptStart, 2)
            | 0x42uy (* 'B' *) -> Some (BoundaryKind.CommandStart, 2)
            | 0x43uy (* 'C' *) -> Some (BoundaryKind.OutputStart, 2)
            | 0x44uy (* 'D' *) ->
                // Kind D may carry an optional exit code in
                // parms.[2]. If parms.[2] exists and parses
                // as Int32, use it; otherwise (missing or
                // malformed) emit CommandFinished None +
                // start key=value parsing at index 2.
                if parms.Length > 2 then
                    let exitCodeStr = decodeAscii parms.[2]
                    match Int32.TryParse(exitCodeStr) with
                    | true, code ->
                        Some (BoundaryKind.CommandFinished (Some code), 3)
                    | false, _ ->
                        // Malformed exit code → preserve
                        // boundary but drop the code. Param
                        // index stays at 2 so a malformed
                        // "exit code" that's actually a
                        // key=value pair still gets parsed.
                        if exitCodeStr.Contains('=') then
                            Some (BoundaryKind.CommandFinished None, 2)
                        else
                            Some (BoundaryKind.CommandFinished None, 3)
                else
                    Some (BoundaryKind.CommandFinished None, 2)
            | _ -> None

    /// Try to parse an OSC 133 payload into a
    /// `PromptBoundaryData` event. Returns `None` for
    /// malformed inputs; consumers silently drop those.
    ///
    /// **Caller contract**: `parms.[0]` should be `"133"B`
    /// (the OSC type discriminator). The parser at
    /// `Screen.Apply`'s OscDispatch arm filters on this
    /// before calling `tryParse`; defence-in-depth here
    /// rejects non-133 inputs.
    let tryParse
            (parms: byte[][])
            (detectedAt: DateTime)
            : PromptBoundaryData option
            =
        // Defensive: empty or single-param payloads can't be
        // OSC 133 (need both type discriminator + kind).
        if parms.Length < 2 then None
        elif parms.[0].Length <> 3
             || parms.[0].[0] <> 0x31uy (* '1' *)
             || parms.[0].[1] <> 0x33uy (* '3' *)
             || parms.[0].[2] <> 0x33uy (* '3' *)
        then None
        else
            match parseKind parms.[1] parms with
            | None -> None
            | Some (kind, extrasStartIdx) ->
                // Parse key=value parameters from
                // parms.[extrasStartIdx..]. Hoist `aid=<id>`
                // to CommandId; everything else goes to
                // ExtraParams.
                let mutable commandId : string option = None
                let mutable extras = Map.empty<string, string>
                for i in extrasStartIdx .. parms.Length - 1 do
                    match trySplitKeyValue parms.[i] with
                    | Some (key, value) ->
                        if key = "aid" then
                            commandId <- Some value
                        else
                            extras <- Map.add key value extras
                    | None ->
                        // Malformed key=value (no '='); skip
                        // silently per the OSC silent-drop
                        // convention.
                        ()
                Some
                    { Kind = kind
                      Source = BoundarySource.Osc133
                      DetectedAt = detectedAt
                      CommandId = commandId
                      ExtraParams = extras
                      // Tier 1.E: parser has no screen access;
                      // `Program.fs.handlePromptBoundary`
                      // augments OSC 133 boundaries with the
                      // cursor row's text via fresh
                      // `screen.SnapshotRows` capture.
                      MatchedRowText = None
                      // Tier 1.E2.A: parser has no screen
                      // access; `Program.fs.handlePromptBoundary`
                      // augments OSC 133 boundaries with the
                      // cursor row index alongside MatchedRowText.
                      MatchedRowIndex = None }
