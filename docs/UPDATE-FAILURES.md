# Stage 11 update-failure announcements (NVDA reference)

This is a reference for what NVDA tells you when `Ctrl+Shift+U`
(the Stage 11 in-app self-update) fails. Each failure mode
produces a distinct, structured announcement that names the
likely cause so you can decide whether to retry, check your
network, or investigate further.

The mapping below is the contract from `src/Terminal.App/Program.fs`
`runUpdateFlow` (PR #66's structured exception handling). If
an announcement here surprises you, the source-of-truth is
that function.

## What you might hear

### "Update check failed: cannot reach GitHub Releases. Check your internet connection."

**Cause.** A network-layer failure — DNS resolution failure,
connection refused, TLS handshake failure, transport-level
error. The `HttpRequestException` from `System.Net.Http`.

**What to do.** Check your internet connection. Common cases:
you're offline; you're behind a captive-portal Wi-Fi that
hasn't been authenticated; your firewall is blocking
`api.github.com` or `objects.githubusercontent.com`. Once the
network is back, press `Ctrl+Shift+U` again.

**What this is NOT.** This is not a SmartScreen prompt or a
signature failure. The unsigned-preview line doesn't yet
verify Authenticode or Ed25519 signatures (see `SECURITY.md`).
A signature failure (when signing returns at v0.1.0+) will
have a distinct announcement.

### "Update check timed out. Check your internet connection and try Ctrl+Shift+U again."

**Cause.** The HTTP request started but didn't complete in
time, or the connection was dropped mid-download. The
`TaskCanceledException` (which `HttpClient` raises when its
internal timeout fires) — Velopack uses `CancellationToken`
under the hood; a dropped connection mid-download lands
here.

**What to do.** Try again. Common cases: a slow network
that exceeded HTTP's default timeout; a network drop midway
through the delta-nupkg download. Velopack does not
auto-resume; pressing `Ctrl+Shift+U` again starts a fresh
request.

### "Update could not be written to disk: \<reason\>. Free up space or check folder permissions in %LocalAppData%\pty-speak\."

**Cause.** A disk-side failure during download or applying
the patch. The `IOException` from `System.IO`. Typically
"out of disk space" or "permission denied" on the install
directory.

**What to do.** Free up disk space (Velopack writes the delta
nupkg to a temp directory and the new install version to
`%LocalAppData%\pty-speak\`; both need free space). If
permissions are the issue, that's unusual for a per-user
install — check that nothing is holding `Terminal.App.exe`
open via something other than the running app you're updating.

### "Update failed: \<message\>"

**Cause.** Anything not matched by the three cases above.
The catch-all branch in `runUpdateFlow`. The `<message>` is
whatever `.Message` the unexpected exception carried.

**What to do.** Note the message and file an issue. This is
the path that catches new Velopack exception types (e.g.
`SignatureMismatch` once signing returns), runtime
unexpected behaviours, or genuinely novel failure modes.
Specific announcements for new exception types should be
added to `runUpdateFlow` (and to this doc) as their failure
modes become observable in the field.

### "Update already in progress; ignoring repeat keypress."

**Cause.** A second `Ctrl+Shift+U` keypress while the first
update flow is still running. Not a failure — the in-progress
guard (`updateInProgress` in `runUpdateFlow`) suppresses
concurrent updates so the second invocation doesn't kick off
parallel `UpdateManager` tasks.

**What to do.** Wait for the first flow to complete (NVDA
will announce "Restarting to apply update" when it does, or
one of the failure announcements above). If nothing
announces for a long time, the first flow may be stuck on
network — restart the app and try again.

### "Auto-update is only available in installed builds. Use scripts/install-latest-preview.ps1 for development copies."

**Cause.** You ran the app from a development build
(`dotnet run` or directly from `src/Terminal.App/bin/`)
rather than from a Velopack-installed location. `IsInstalled`
on the `UpdateManager` returns `false`, and `runUpdateFlow`
short-circuits with this announcement to fail gracefully.

**What to do.** For dev workflow, this is expected — your
running build IS the source. For installed builds, see
`docs/RELEASE-PROCESS.md` for the install path expectations
(Velopack writes to `%LocalAppData%\pty-speak\`).

## Related

- Source: `src/Terminal.App/Program.fs` (`runUpdateFlow`).
- Tests: see PR-C's `tests/Tests.Unit/UpdateFlowTests.fs`
  (in-flight at the time this doc lands; may not exist yet).
- Threat model for the update path: `SECURITY.md` "Auto-update
  threat model" section (T-1 through T-9).
- Release flow that produces the artifacts these failures
  reference: `docs/RELEASE-PROCESS.md`.
- Manual smoke matrix: `docs/ACCESSIBILITY-TESTING.md` "Stage
  11" section.
