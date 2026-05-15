module PtySpeak.Tests.Unit.SessionSanitiserTests

open System
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open Terminal.Core

// ---------------------------------------------------------------------
// Cycle 24d-2 — SessionSanitiser behavioural pinning
// ---------------------------------------------------------------------
//
// SessionSanitiser holds module-level mutable state (the
// registered list). Each test calls `clear ()` first to isolate
// from sibling tests + any startup `registerFromEnvironment`
// (the test process inherits the env from the runner, which on
// CI may include real credentials matching the deny-list — we
// don't want those leaking through).
//
// Tests pin:
//   * MinValueLength threshold.
//   * Empty/whitespace value handling.
//   * Deny-list pattern parity with Stage 7's Native.fs.
//   * Single-value redaction.
//   * Multiple-value redaction.
//   * Substring-overlap safety (longer value wins).
//   * Per-tuple field application (CommandText, OutputText,
//     PromptText, ExtraParams values).
//   * registerFromEnvironment populates from process env.
//   * clear empties the registered list.
//   * Marker format `<REDACTED:UPPERCASE_NAME>`.

// ---------------------------------------------------------------------
// Test fixtures
// ---------------------------------------------------------------------

let private fixedTuple () : SessionModel.IOCell =
    { Id = Guid.Parse("11111111-2222-3333-4444-555555555555")
      CellSequence = 0L
      CommandId = None
      Phase = SessionModel.IOCellPhase.Sealed
      ShellId = "powershell"
      PromptStartedAt = DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc)
      CommandStartedAt = None
      OutputStartedAt = None
      CommandFinishedAt =
        Some (DateTime(2026, 5, 9, 12, 0, 1, DateTimeKind.Utc))
      PromptText = "PS>"
      CommandText = ""
      OutputText = ""
      ExitCode = Some 0
      Sources = Map.empty
      ExtraParams = Map.empty }

let private resetSanitiser () : unit =
    SessionSanitiser.clear ()

// ---------------------------------------------------------------------
// MinValueLength threshold
// ---------------------------------------------------------------------

[<Fact>]
let ``MinValueLength constant equals 16`` () =
    // Pinned: future changes to the threshold must update this
    // test deliberately.
    Assert.Equal(16, SessionSanitiser.MinValueLength)

[<Fact>]
let ``register skips values shorter than MinValueLength`` () =
    resetSanitiser ()
    SessionSanitiser.register "short" "BANK_API_KEY"
    let text = "the password is short"
    Assert.Equal(text, SessionSanitiser.sanitise text)

[<Fact>]
let ``register accepts values exactly at MinValueLength`` () =
    resetSanitiser ()
    let value = "abcdefghijklmnop"   // exactly 16 chars
    Assert.Equal(16, value.Length)
    SessionSanitiser.register value "MY_TOKEN"
    let text = sprintf "echo %s" value
    Assert.Equal(
        "echo <REDACTED:MY_TOKEN>",
        SessionSanitiser.sanitise text)

[<Fact>]
let ``register skips empty / whitespace values`` () =
    resetSanitiser ()
    SessionSanitiser.register "" "EMPTY_KEY"
    SessionSanitiser.register "   " "WHITESPACE_KEY"
    SessionSanitiser.register "\t\n" "TAB_KEY"
    Assert.Equal(0, SessionSanitiser.registeredCount ())

// ---------------------------------------------------------------------
// Marker format
// ---------------------------------------------------------------------

[<Fact>]
let ``marker uses uppercase name and angle-bracket format`` () =
    resetSanitiser ()
    SessionSanitiser.register
        "github_pat_aaaaaaaaaaaaaaaaaaaa" "github_token"
    let result =
        SessionSanitiser.sanitise
            "token=github_pat_aaaaaaaaaaaaaaaaaaaa rest"
    Assert.Equal("token=<REDACTED:GITHUB_TOKEN> rest", result)

// ---------------------------------------------------------------------
// Single-value redaction + leaves unrelated text alone
// ---------------------------------------------------------------------

[<Fact>]
let ``sanitise replaces a registered value with the marker`` () =
    resetSanitiser ()
    let secret = "ghp_abcdefghijklmnopqrstuvwxyz"
    SessionSanitiser.register secret "GITHUB_TOKEN"
    Assert.Equal(
        sprintf "echo <REDACTED:GITHUB_TOKEN>",
        SessionSanitiser.sanitise (sprintf "echo %s" secret))

[<Fact>]
let ``sanitise leaves text without registered values unchanged`` () =
    resetSanitiser ()
    SessionSanitiser.register
        "ghp_abcdefghijklmnopqrstuvwxyz" "GITHUB_TOKEN"
    let text = "ls -la /usr/local/bin"
    Assert.Equal(text, SessionSanitiser.sanitise text)

[<Fact>]
let ``sanitise is a no-op on an empty registered list`` () =
    resetSanitiser ()
    let text = "anything goes here"
    Assert.Equal(text, SessionSanitiser.sanitise text)

// ---------------------------------------------------------------------
// Multiple-value redaction
// ---------------------------------------------------------------------

[<Fact>]
let ``sanitise handles multiple distinct registered values in one string`` () =
    resetSanitiser ()
    let token = "ghp_abcdefghijklmnopqrstuvwxyz"
    let key = "AKIAIOSFODNN7EXAMPLE_aaaa"
    SessionSanitiser.register token "GITHUB_TOKEN"
    SessionSanitiser.register key "AWS_ACCESS_KEY"
    let text = sprintf "%s and %s leaked" token key
    Assert.Equal(
        "<REDACTED:GITHUB_TOKEN> and <REDACTED:AWS_ACCESS_KEY> leaked",
        SessionSanitiser.sanitise text)

[<Fact>]
let ``sanitise handles the same value appearing multiple times`` () =
    resetSanitiser ()
    let secret = "secret_value_that_is_long_enough_to_register"
    SessionSanitiser.register secret "MY_TOKEN"
    let text = sprintf "%s and %s and %s" secret secret secret
    Assert.Equal(
        "<REDACTED:MY_TOKEN> and <REDACTED:MY_TOKEN> and <REDACTED:MY_TOKEN>",
        SessionSanitiser.sanitise text)

// ---------------------------------------------------------------------
// Substring-overlap safety
// ---------------------------------------------------------------------

[<Fact>]
let ``sanitise replaces longer values before shorter (substring safety)`` () =
    resetSanitiser ()
    let shorter = "abcdefghijklmnop"   // 16 chars
    let longer = "abcdefghijklmnopqrstuvwx"   // 24 chars (extends shorter)
    SessionSanitiser.register shorter "SHORT_KEY"
    SessionSanitiser.register longer "LONG_KEY"
    // Text containing only the longer string MUST redact as
    // <REDACTED:LONG_KEY>, not <REDACTED:SHORT_KEY>qrstuvwx.
    let text = sprintf "value: %s" longer
    Assert.Equal(
        "value: <REDACTED:LONG_KEY>",
        SessionSanitiser.sanitise text)

// ---------------------------------------------------------------------
// Stage 7 deny-list parity (isDenied)
// ---------------------------------------------------------------------

[<Fact>]
let ``isDenied matches *_TOKEN`` () =
    Assert.True(SessionSanitiser.isDenied "GITHUB_TOKEN")
    Assert.True(SessionSanitiser.isDenied "github_token")
    Assert.True(SessionSanitiser.isDenied "MyApp_Token")

[<Fact>]
let ``isDenied matches *_SECRET`` () =
    Assert.True(SessionSanitiser.isDenied "OAUTH_SECRET")
    Assert.True(SessionSanitiser.isDenied "client_secret")

[<Fact>]
let ``isDenied matches *_KEY`` () =
    Assert.True(SessionSanitiser.isDenied "AWS_ACCESS_KEY")
    Assert.True(SessionSanitiser.isDenied "api_key")

[<Fact>]
let ``isDenied matches *_PASSWORD`` () =
    Assert.True(SessionSanitiser.isDenied "DB_PASSWORD")
    Assert.True(SessionSanitiser.isDenied "ssh_password")

[<Fact>]
let ``isDenied does NOT match unrelated names`` () =
    Assert.False(SessionSanitiser.isDenied "PATH")
    Assert.False(SessionSanitiser.isDenied "HOME")
    Assert.False(SessionSanitiser.isDenied "USER")
    // Confirm the suffix-match is anchored: `KEYBOARD_LAYOUT`
    // does NOT match `*_KEY` (no leading underscore before
    // KEY), per Stage 7's design.
    Assert.False(SessionSanitiser.isDenied "KEYBOARD_LAYOUT")
    Assert.False(SessionSanitiser.isDenied "KEYS_TO_THE_KINGDOM")

[<Fact>]
let ``isDenied exempts ANTHROPIC_API_KEY (Claude Code primary credential)`` () =
    Assert.False(SessionSanitiser.isDenied "ANTHROPIC_API_KEY")
    Assert.False(SessionSanitiser.isDenied "anthropic_api_key")

// ---------------------------------------------------------------------
// sanitiseTuple
// ---------------------------------------------------------------------

[<Fact>]
let ``sanitiseTuple sanitises CommandText and OutputText`` () =
    resetSanitiser ()
    let secret = "ghp_abcdefghijklmnopqrstuvwxyz"
    SessionSanitiser.register secret "GITHUB_TOKEN"
    let tuple =
        { fixedTuple () with
            CommandText = sprintf "echo %s" secret
            OutputText = sprintf "echoed: %s" secret }
    let sanitised = SessionSanitiser.sanitiseTuple tuple
    Assert.Equal("echo <REDACTED:GITHUB_TOKEN>", sanitised.CommandText)
    Assert.Equal("echoed: <REDACTED:GITHUB_TOKEN>", sanitised.OutputText)

[<Fact>]
let ``sanitiseTuple sanitises PromptText and ExtraParams values`` () =
    resetSanitiser ()
    let secret = "secret_value_long_enough_to_register"
    SessionSanitiser.register secret "PROMPT_TOKEN"
    let tuple =
        { fixedTuple () with
            PromptText = sprintf "[%s] $" secret
            ExtraParams =
                Map.ofList [ "k", sprintf "param=%s" secret ] }
    let sanitised = SessionSanitiser.sanitiseTuple tuple
    Assert.Equal("[<REDACTED:PROMPT_TOKEN>] $", sanitised.PromptText)
    Assert.Equal(
        "param=<REDACTED:PROMPT_TOKEN>",
        Map.find "k" sanitised.ExtraParams)

[<Fact>]
let ``sanitiseTuple leaves non-text fields unchanged`` () =
    resetSanitiser ()
    SessionSanitiser.register
        "ghp_abcdefghijklmnopqrstuvwxyz" "GITHUB_TOKEN"
    let original = fixedTuple ()
    let sanitised = SessionSanitiser.sanitiseTuple original
    // No secrets in original's text fields → text unchanged
    // AND non-text fields unchanged.
    Assert.Equal(original.Id, sanitised.Id)
    Assert.Equal(original.ShellId, sanitised.ShellId)
    Assert.Equal(original.PromptStartedAt, sanitised.PromptStartedAt)
    Assert.Equal(original.CommandFinishedAt, sanitised.CommandFinishedAt)
    Assert.Equal(original.ExitCode, sanitised.ExitCode)

[<Fact>]
let ``sanitiseTuple ExtraParams keys pass through unchanged (only values are sanitised)`` () =
    resetSanitiser ()
    let secret = "secret_value_long_enough_to_register"
    SessionSanitiser.register secret "API_KEY"
    let tuple =
        { fixedTuple () with
            ExtraParams =
                Map.ofList
                    [ "key1", "innocent value"
                      "key2", sprintf "with %s inside" secret ] }
    let sanitised = SessionSanitiser.sanitiseTuple tuple
    Assert.True(Map.containsKey "key1" sanitised.ExtraParams)
    Assert.True(Map.containsKey "key2" sanitised.ExtraParams)
    Assert.Equal(
        "innocent value",
        Map.find "key1" sanitised.ExtraParams)
    Assert.Equal(
        "with <REDACTED:API_KEY> inside",
        Map.find "key2" sanitised.ExtraParams)

// ---------------------------------------------------------------------
// clear
// ---------------------------------------------------------------------

[<Fact>]
let ``clear empties the registered list`` () =
    resetSanitiser ()
    let secret = "ghp_abcdefghijklmnopqrstuvwxyz"
    SessionSanitiser.register secret "GITHUB_TOKEN"
    Assert.True(SessionSanitiser.registeredCount () > 0)
    let textBefore = sprintf "echo %s" secret
    Assert.NotEqual<string>(
        textBefore, SessionSanitiser.sanitise textBefore)
    SessionSanitiser.clear ()
    Assert.Equal(0, SessionSanitiser.registeredCount ())
    Assert.Equal(textBefore, SessionSanitiser.sanitise textBefore)

// ---------------------------------------------------------------------
// registerFromEnvironment
// ---------------------------------------------------------------------

[<Fact>]
let ``registerFromEnvironment registers values for matching env-var names`` () =
    resetSanitiser ()
    // Capture and restore: matches the FileLoggerTests env-var
    // isolation pattern. Avoids passing a literal `null` to
    // SetEnvironmentVariable (F# 9 nullness friendly).
    let varName = "PTYSPEAK_TEST_TOKEN"
    let original = Environment.GetEnvironmentVariable(varName)
    let testValue = "test_secret_value_aaaaaaaaaaaaaaaaaaaaaaaaa"
    Environment.SetEnvironmentVariable(varName, testValue)
    try
        let count =
            SessionSanitiser.registerFromEnvironment
                NullLogger.Instance
        // At least our test value should register; CI runners
        // may have other matching env vars too.
        Assert.True(count >= 1)
        // The value should now redact.
        Assert.Equal(
            "echo <REDACTED:PTYSPEAK_TEST_TOKEN>",
            SessionSanitiser.sanitise (sprintf "echo %s" testValue))
    finally
        Environment.SetEnvironmentVariable(varName, original)
        resetSanitiser ()

[<Fact>]
let ``registerFromEnvironment skips non-deny-listed names`` () =
    resetSanitiser ()
    let varName = "PTYSPEAK_TEST_NORMAL_VAR"
    let original = Environment.GetEnvironmentVariable(varName)
    let testValue = "ordinary_long_value_aaaaaaaaaaaaaaaaa"
    Environment.SetEnvironmentVariable(varName, testValue)
    try
        SessionSanitiser.registerFromEnvironment
            NullLogger.Instance
        |> ignore
        // The non-deny-listed value should pass through.
        Assert.Equal(
            sprintf "echo %s" testValue,
            SessionSanitiser.sanitise (sprintf "echo %s" testValue))
    finally
        Environment.SetEnvironmentVariable(varName, original)
        resetSanitiser ()

[<Fact>]
let ``registerFromEnvironment skips short values`` () =
    resetSanitiser ()
    let varName = "PTYSPEAK_TEST_SHORT_KEY"
    let original = Environment.GetEnvironmentVariable(varName)
    Environment.SetEnvironmentVariable(varName, "abc")
    try
        SessionSanitiser.registerFromEnvironment
            NullLogger.Instance
        |> ignore
        // Short value not registered; "abc" passes through.
        Assert.Equal(
            "echo abc",
            SessionSanitiser.sanitise "echo abc")
    finally
        Environment.SetEnvironmentVariable(varName, original)
        resetSanitiser ()
