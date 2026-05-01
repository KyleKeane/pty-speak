namespace Terminal.Core

open System
open System.Net.Http
open System.Threading.Tasks

/// User-facing announcement messages for Stage 11's
/// `runUpdateFlow` exception handler. Extracted from
/// `src/Terminal.App/Program.fs` (audit-cycle PR-D) so the
/// regression-class that matters most — the user-visible
/// message NVDA narrates when an update fails — is
/// unit-testable without standing up a Velopack mock.
///
/// Pure function. Takes the exception, returns the string.
/// `runUpdateFlow`'s catch block calls this and pipes the
/// result to `TerminalView.Announce`.
module UpdateMessages =

    /// Map an exception from the update flow to the
    /// announcement string NVDA reads. The four cases
    /// match what PR #66 introduced as structured handling:
    ///
    ///   * `HttpRequestException` — the most common case
    ///     (network unreachable, DNS failure, TLS handshake,
    ///     captive-portal Wi-Fi). Includes the inner message
    ///     so the user can distinguish "no internet" from
    ///     "GitHub is down."
    ///   * `TaskCanceledException` — HTTP timeout or
    ///     mid-download drop. Bare message; no `ex.Message`
    ///     because the framework's text isn't user-friendly.
    ///   * `IOException` — disk-side failure during download
    ///     or apply. Includes inner message + a hint to free
    ///     space or check `%LocalAppData%\pty-speak\`
    ///     permissions.
    ///   * Catch-all — anything else. Generic "Update
    ///     failed" + the inner message so the user has a
    ///     thread to pull on if they file an issue.
    ///
    /// New Velopack exception types (e.g. `SignatureMismatch`
    /// once signing returns) get added here as discrete
    /// branches before the catch-all when their failure mode
    /// becomes observable.
    let announcementForException (ex: exn) : string =
        match ex with
        | :? HttpRequestException ->
            sprintf
                "Update check failed: cannot reach GitHub Releases. Check your internet connection. (%s)"
                ex.Message
        | :? TaskCanceledException ->
            "Update check timed out. Check your internet connection and try Ctrl+Shift+U again."
        | :? System.IO.IOException ->
            sprintf
                "Update could not be written to disk: %s. Free up space or check folder permissions in %%LocalAppData%%\\pty-speak\\."
                ex.Message
        | _ ->
            sprintf "Update failed: %s" ex.Message
