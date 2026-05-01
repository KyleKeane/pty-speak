module PtySpeak.Tests.Unit.UpdateMessagesTests

open System
open System.Net.Http
open System.Threading.Tasks
open Xunit
open Terminal.Core

/// Tests for `Terminal.Core.UpdateMessages.announcementForException`,
/// the pure function PR-D extracted from `runUpdateFlow`'s catch
/// block (audit-cycle follow-up; SESSION-HANDOFF.md item 6).
/// The function maps an exception to the NVDA announcement
/// string; these tests pin the contract per failure class so a
/// future refactor that accidentally collapses the branches
/// (or returns the wrong message for a class) fails CI loudly.

[<Fact>]
let ``HttpRequestException produces network-failure announcement with inner message`` () =
    let inner = "Could not resolve host 'github.com'"
    let ex = HttpRequestException(inner)
    let msg = UpdateMessages.announcementForException ex
    Assert.Contains("cannot reach GitHub Releases", msg)
    Assert.Contains("Check your internet connection", msg)
    Assert.Contains(inner, msg)

[<Fact>]
let ``TaskCanceledException produces timeout announcement without inner message`` () =
    let ex = TaskCanceledException()
    let msg = UpdateMessages.announcementForException ex
    Assert.Contains("Update check timed out", msg)
    Assert.Contains("Ctrl+Shift+U", msg)

[<Fact>]
let ``IOException produces disk-failure announcement with inner message and path hint`` () =
    let inner = "The process cannot access the file because it is being used by another process."
    let ex = System.IO.IOException(inner)
    let msg = UpdateMessages.announcementForException ex
    Assert.Contains("could not be written to disk", msg)
    Assert.Contains(inner, msg)
    // The path hint mentions %LocalAppData%\pty-speak\ — a
    // user with NVDA can't see the path but it gives
    // sighted helpers something to navigate to.
    Assert.Contains("LocalAppData", msg)
    Assert.Contains("pty-speak", msg)

[<Fact>]
let ``Generic exception falls through to catch-all with inner message`` () =
    // Pick an exception type the function doesn't have a
    // specific branch for. ArgumentException is unlikely to
    // come from Velopack's update flow but exercises the
    // catch-all branch.
    let inner = "Unexpected internal state"
    let ex = ArgumentException(inner)
    let msg = UpdateMessages.announcementForException ex
    Assert.StartsWith("Update failed:", msg)
    Assert.Contains(inner, msg)

[<Fact>]
let ``HttpRequestException is matched specifically, not by the catch-all`` () =
    // Defensive — if a refactor reorders the match arms and
    // the catch-all moves above HttpRequestException, this
    // test fails because the catch-all message starts with
    // "Update failed:" but the HTTP-specific message starts
    // with "Update check failed:".
    let ex = HttpRequestException("test")
    let msg = UpdateMessages.announcementForException ex
    Assert.False(
        msg.StartsWith("Update failed:"),
        "HttpRequestException landed on the catch-all branch, not the specific one. Match-arm ordering regressed.")

[<Fact>]
let ``IOException is matched specifically, not by the catch-all`` () =
    let ex = System.IO.IOException("test")
    let msg = UpdateMessages.announcementForException ex
    Assert.False(
        msg.StartsWith("Update failed:"),
        "IOException landed on the catch-all branch, not the specific one. Match-arm ordering regressed.")

// ---------- Control-character sanitisation (audit-cycle SR-2) ----------
//
// `announcementForException` interpolates `ex.Message` into the
// announcement string NVDA reads via UIA's
// `RaiseNotificationEvent`. SR-2 added a chokepoint --
// `AnnounceSanitiser.sanitise` -- that strips C0 controls, DEL,
// and C1 controls before interpolation, so an exception message
// containing BiDi overrides, BEL, or ANSI escape sequences
// cannot confuse NVDA's notification handler. See SECURITY.md
// row TC-5.

[<Fact>]
let ``BiDi override in exception message is stripped before announcement`` () =
    // U+202E RIGHT-TO-LEFT OVERRIDE is a printable Unicode
    // character; the sanitiser strips C0/DEL/C1 only, not
    // U+202E. Pin the contract: the *control-byte* class is
    // gone; printable BiDi characters survive (Stage 5 may
    // revisit if we want a stricter Unicode policy). Source
    // uses \u202E / \u202C escapes to keep the file plain
    // ASCII (Trojan-Source warning).
    let inner = "host \u202Emoc.lave@\u202C resolves wrong"
    let ex = HttpRequestException(inner)
    let msg = UpdateMessages.announcementForException ex
    // No C0 / C1 / DEL bytes anywhere in the output.
    let hasControlByte =
        msg
        |> Seq.exists (fun ch ->
            let code = int ch
            code < 0x20 || code = 0x7F || (code >= 0x80 && code <= 0x9F))
    Assert.False(hasControlByte, "Announcement must not contain C0/DEL/C1 control bytes.")
