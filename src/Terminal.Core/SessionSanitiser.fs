namespace Terminal.Core

open System
open System.Collections.Generic
open System.Text
open Microsoft.Extensions.Logging

/// Cycle 24d-2 — env-var VALUE-based sanitiser for IOCell
/// persistence. Captures the values of deny-listed env vars at
/// startup, then redacts those values wherever they appear in
/// persisted tuple text fields.
///
/// **Threat model**: a shell expands an env var into output. For
/// example, `echo $GITHUB_TOKEN` causes the shell to substitute
/// the literal token value `ghp_abc123...` into stdout. The
/// terminal sees the expanded value (NOT the variable name); the
/// SessionLogWriter would then persist that expanded value to
/// `session-<id>.jsonl`. This sanitiser substitutes the value
/// with the marker `<REDACTED:GITHUB_TOKEN>` before write, so
/// the on-disk artefact never contains the secret.
///
/// **NOT a NAME-based sanitiser**. Stage 7's env-scrub at the
/// child-process boundary (`src/Terminal.Pty/Native.fs:449-465`)
/// works at process-creation time — it strips deny-listed names
/// from the spawned shell's environment block so the shell can't
/// expand them in the first place. This sanitiser is the
/// complement: it handles the case where the env var WAS in the
/// parent's env (so pty-speak inherited it) and the user's
/// command echoed the value into the persisted output.
///
/// **Deny-list pattern**: lifted verbatim from
/// `Terminal.Pty.Native.isDenied`. Suffix match on uppercase
/// names: `*_TOKEN`, `*_SECRET`, `*_KEY`, `*_PASSWORD`. Single
/// exemption: `ANTHROPIC_API_KEY` (Claude Code's primary
/// credential).
///
/// **Min-length threshold**: 16 chars. A short env-var value
/// like `BANK_API_KEY=admin` (5 chars) would NOT register;
/// otherwise we'd redact every occurrence of "admin" in any
/// command output, which is absurd.
///
/// **Redaction marker format**: `<REDACTED:UPPERCASE_NAME>`.
/// Decades-stable; angle brackets are unambiguous in shell
/// output (rare); colon-delimited for regex parseability by
/// future replay tools.
///
/// **Substring-overlap safety**: registered values are sorted
/// by length DESCENDING so that if one secret value is a
/// substring of another, the longer one is redacted first.
///
/// **Where applied**: `SessionLogWriter.writeOne` calls
/// `sanitiseTuple` on every cell before formatIOCellAsJsonl.
/// The substrate's in-memory History keeps unsanitised text
/// (the user can recover their own commands via Ctrl+Shift+Y);
/// only the persistence layer redacts. This is the layered
/// design — substrate stays honest, persistence boundary
/// applies the privacy policy.
///
/// **Threading**: `register`, `sanitise`, and `sanitiseTuple`
/// guard the registered list with a lock. Registration
/// typically happens once at startup but the lock makes the
/// API safe to call from anywhere. `clear` is intended for
/// test isolation only.
[<RequireQualifiedAccess>]
module SessionSanitiser =

    /// Minimum value length for registration. Values shorter
    /// than this are silently dropped to avoid spamming
    /// redactions on common short values. 16 chars is short
    /// enough to capture realistic credentials (most modern
    /// API keys are 32+ chars; even legacy ones tend to be
    /// 20-40 chars) while filtering out things like
    /// `BANK_API_KEY=admin` that would otherwise trigger
    /// hilarious false-positive redactions on the literal
    /// word "admin".
    [<Literal>]
    let MinValueLength : int = 16

    /// Suffix-match deny-list. Mirrors
    /// `Terminal.Pty.Native.isDenied` exactly (Stage 7 PO-5).
    /// Single exemption: `ANTHROPIC_API_KEY` (Claude Code's
    /// primary credential workload). Internal so tests can
    /// reach it.
    let internal isDenied (name: string) : bool =
        let n = name.ToUpperInvariant()
        if n = "ANTHROPIC_API_KEY" then false
        else
            n.EndsWith("_TOKEN", StringComparison.Ordinal)
            || n.EndsWith("_SECRET", StringComparison.Ordinal)
            || n.EndsWith("_KEY", StringComparison.Ordinal)
            || n.EndsWith("_PASSWORD", StringComparison.Ordinal)

    /// Build the redaction marker for a registered env-var
    /// name. Stable across versions per the
    /// SESSION-MODEL.md "On-disk wire format" section.
    let internal markerFor (name: string) : string =
        sprintf "<REDACTED:%s>" (name.ToUpperInvariant())

    // Internal mutable state. Tuples are (value, marker). We
    // sort by value-length DESC so that if one secret is a
    // substring of another, the longer one is matched first.
    // Lock guards mutation + iteration.
    let private gate = obj ()
    let mutable private registered : (string * string) list = []

    /// Register one (value, name) pair. Silently skips when
    /// the value is shorter than `MinValueLength`, empty, or
    /// whitespace. Idempotent on duplicates (re-registering
    /// the same value just updates the marker).
    let register (value: string) (name: string) : unit =
        if String.IsNullOrWhiteSpace(value) then ()
        elif value.Length < MinValueLength then ()
        else
            lock gate (fun () ->
                let marker = markerFor name
                let withoutDup =
                    registered |> List.filter (fun (v, _) -> v <> value)
                let appended = (value, marker) :: withoutDup
                registered <-
                    appended
                    |> List.sortByDescending (fun (v, _) -> v.Length))

    /// Enumerate the process environment, register every
    /// matching deny-listed env var's value, return the count
    /// of successful registrations. Per LOGGING.md ("log
    /// counts, never names or values"), the count is logged
    /// at Information level by the caller.
    ///
    /// Idempotent: subsequent calls re-scan the env and
    /// register any new matches; existing registrations stay.
    /// Use `clear` for test isolation between runs.
    let registerFromEnvironment (logger: ILogger) : int =
        let envVars = Environment.GetEnvironmentVariables()
        let mutable count = 0
        let enumerator = envVars.GetEnumerator()
        try
            while enumerator.MoveNext() do
                let entry = enumerator.Entry
                match entry.Key, entry.Value with
                | (:? string as name), (:? string as value) ->
                    if isDenied name then
                        let lengthBefore =
                            lock gate (fun () -> registered.Length)
                        register value name
                        let lengthAfter =
                            lock gate (fun () -> registered.Length)
                        if lengthAfter > lengthBefore then
                            count <- count + 1
                | _ -> ()
        finally
            match enumerator with
            | :? IDisposable as d -> d.Dispose()
            | _ -> ()
        logger.LogInformation(
            "SessionSanitiser: registered {Count} env-var-value redaction patterns from process environment.",
            count)
        count

    /// Apply all registered redactions to `text`. Pure;
    /// returns the input unchanged if no registered values
    /// occur. Replacement is case-sensitive ordinal
    /// substring match (env-var values are typically
    /// case-sensitive credentials).
    let sanitise (text: string) : string =
        if String.IsNullOrEmpty(text) then text
        else
            let snapshot =
                lock gate (fun () -> registered)
            if List.isEmpty snapshot then text
            else
                let sb = StringBuilder(text)
                for (value, marker) in snapshot do
                    sb.Replace(value, marker) |> ignore
                sb.ToString()

    /// Apply redactions to all text-bearing fields of a
    /// IOCell. Returns a new tuple with `CommandText`,
    /// `OutputText`, `PromptText`, and every `ExtraParams`
    /// value sanitised. Conservative: anything that could
    /// carry expanded env-var values gets the treatment.
    /// `Id`, timestamps, `ShellId`, `CommandId`, `ExitCode`,
    /// and `Sources` are passed through unchanged (they
    /// can't carry user-controlled content).
    let sanitiseTuple
            (tuple: SessionModel.IOCell)
            : SessionModel.IOCell
            =
        { tuple with
            PromptText = sanitise tuple.PromptText
            CommandText = sanitise tuple.CommandText
            OutputText = sanitise tuple.OutputText
            ExtraParams =
                tuple.ExtraParams
                |> Map.map (fun _ v -> sanitise v) }

    /// Reset the registered list. Test-only — production code
    /// never calls this. Used by `SessionSanitiserTests.fs`
    /// to isolate per-test state given the module-level
    /// mutable.
    let clear () : unit =
        lock gate (fun () ->
            registered <- [])

    /// Internal — peek at the current registered count for
    /// tests that need to assert "exactly N values are
    /// registered" without inspecting the values themselves
    /// (which would defeat the privacy contract).
    let internal registeredCount () : int =
        lock gate (fun () -> registered.Length)
