module PtySpeak.Tests.Unit.EnvBlockTests

open System
open System.Runtime.InteropServices
open System.Text
open Xunit
open Terminal.Pty.Native

// ---------------------------------------------------------------------
// Stage 7 PR-A — EnvBlock allow-list / deny-list / marshalling pinning
// ---------------------------------------------------------------------
//
// `EnvBlock.build` is the env-scrub PO-5 entry point: it produces the
// HGlobal-allocated UTF-16LE environment block `CreateProcess` consumes
// via `lpEnvironment`. These tests pin three contracts:
//
//   1. Assembly rules — allow-list, deny-list (with the
//      `ANTHROPIC_API_KEY` exemption), HOME=%USERPROFILE% fallback,
//      always-set TERM/COLORTERM overriding parent.
//   2. Stripped-count semantics — the count returned for
//      `Information`-level logging matches what the deny-list dropped
//      from the parent map (not "what is missing from the final
//      block").
//   3. Marshalling layout — UTF-16LE bytes, sorted by uppercase name,
//      `NAME=VALUE\u0000` per entry, terminating extra `\u0000`. Get
//      this wrong and the child process sees no environment at all
//      (silent-failure canary per `docs/SESSION-HANDOFF.md` Stage 7
//      sketch §"Known risks").
//
// All non-ASCII test data uses `\u`/`\x` escapes so the source stays
// plain ASCII (matches `tests/Tests.Unit/AnnounceSanitiserTests.fs`
// header note on the F# 9 BiDi / Trojan-Source warning under
// `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

/// Read the full HGlobal block back into a managed `byte[]` for
/// byte-level assertions. Caller is responsible for freeing the
/// pointer separately (typically via `EnvBlock.Built.Block`).
let private readBlock (built: EnvBlock.Built) : byte[] =
    let buf = Array.zeroCreate<byte> built.ByteLength
    Marshal.Copy(built.Block, buf, 0, built.ByteLength)
    buf

/// Decode the marshalled block back into a list of `NAME=VALUE` strings
/// (excluding the terminating empty entry). Lets tests assert on logical
/// content without re-deriving byte offsets.
let private decodeEntries (built: EnvBlock.Built) : string list =
    let bytes = readBlock built
    // Strip trailing double-NUL (block terminator) before splitting.
    let s = Encoding.Unicode.GetString(bytes)
    s.Split('\u0000')
    |> Array.filter (fun e -> e.Length > 0)
    |> Array.toList

/// Free the HGlobal pointer in a `Built` record. Tests that allocate
/// must call this; without it each fixture leaks the block (~hundreds
/// of bytes per call — wouldn't crash the test run but accumulates).
let private freeBuilt (built: EnvBlock.Built) : unit =
    Marshal.FreeHGlobal(built.Block)

// ---------------------------------------------------------------------
// Allow-list preservation (assembly)
// ---------------------------------------------------------------------

[<Fact>]
let ``allow-list preserves PATH USERPROFILE APPDATA LOCALAPPDATA HOME`` () =
    let parent =
        Map.ofList
            [ "PATH", "C:\\Windows;C:\\bin"
              "USERPROFILE", "C:\\Users\\test"
              "APPDATA", "C:\\Users\\test\\AppData\\Roaming"
              "LOCALAPPDATA", "C:\\Users\\test\\AppData\\Local"
              "HOME", "C:\\Users\\test" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built |> Set.ofList
        Assert.Contains("PATH=C:\\Windows;C:\\bin", entries)
        Assert.Contains("USERPROFILE=C:\\Users\\test", entries)
        Assert.Contains("APPDATA=C:\\Users\\test\\AppData\\Roaming", entries)
        Assert.Contains("LOCALAPPDATA=C:\\Users\\test\\AppData\\Local", entries)
        Assert.Contains("HOME=C:\\Users\\test", entries)
    finally freeBuilt built

[<Fact>]
let ``allow-list preserves ANTHROPIC_API_KEY despite *_KEY pattern`` () =
    let parent = Map.ofList [ "ANTHROPIC_API_KEY", "sk-ant-test-1234" ]
    let built = EnvBlock.buildFromMap parent
    try
        Assert.Contains("ANTHROPIC_API_KEY=sk-ant-test-1234", decodeEntries built)
        Assert.Equal(0, built.StrippedCount)
    finally freeBuilt built

[<Fact>]
let ``allow-list preserves CLAUDE_CODE_GIT_BASH_PATH`` () =
    let parent = Map.ofList [ "CLAUDE_CODE_GIT_BASH_PATH", "C:\\Program Files\\Git\\bin\\bash.exe" ]
    let built = EnvBlock.buildFromMap parent
    try
        Assert.Contains(
            "CLAUDE_CODE_GIT_BASH_PATH=C:\\Program Files\\Git\\bin\\bash.exe",
            decodeEntries built)
    finally freeBuilt built

[<Fact>]
let ``out-of-allow-list parent vars are dropped without counting toward stripped`` () =
    // Random user vars not on the allow-list and not on the deny-list
    // are dropped silently. They DON'T count toward StrippedCount —
    // that count tracks deny-list strips only (the security-relevant
    // signal).
    let parent =
        Map.ofList
            [ "PATH", "C:\\Windows"
              "EDITOR", "vim"
              "PAGER", "less"
              "MY_RANDOM_VAR", "42" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built
        Assert.DoesNotContain("EDITOR=vim", entries)
        Assert.DoesNotContain("PAGER=less", entries)
        Assert.DoesNotContain("MY_RANDOM_VAR=42", entries)
        Assert.Contains("PATH=C:\\Windows", entries)
        Assert.Equal(0, built.StrippedCount)
    finally freeBuilt built

// ---------------------------------------------------------------------
// Deny-list pattern matching
// ---------------------------------------------------------------------

[<Fact>]
let ``deny-list strips *_TOKEN, *_SECRET, *_KEY (non-Anthropic), *_PASSWORD`` () =
    let parent =
        Map.ofList
            [ "GITHUB_TOKEN", "ghp_xxx"
              "OPENAI_SECRET", "sk-openai-xxx"
              "AWS_SECRET_ACCESS_KEY", "wJalrXUtnFEMI"
              "DB_PASSWORD", "hunter2"
              "BANK_API_KEY", "bnk_xxx"
              "PATH", "C:\\Windows" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built
        Assert.DoesNotContain("GITHUB_TOKEN=ghp_xxx", entries)
        Assert.DoesNotContain("OPENAI_SECRET=sk-openai-xxx", entries)
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI", entries)
        Assert.DoesNotContain("DB_PASSWORD=hunter2", entries)
        Assert.DoesNotContain("BANK_API_KEY=bnk_xxx", entries)
        Assert.Contains("PATH=C:\\Windows", entries)
        // Five sensitive vars stripped; PATH preserved.
        Assert.Equal(5, built.StrippedCount)
    finally freeBuilt built

[<Fact>]
let ``deny-list does not match KEYBOARD_LAYOUT (no underscore before KEY)`` () =
    // Suffix match must require the leading underscore so common
    // non-secret names like KEYBOARD_LAYOUT, TOKENISER, etc. don't
    // get stripped.
    let parent = Map.ofList [ "KEYBOARD_LAYOUT", "us" ]
    let built = EnvBlock.buildFromMap parent
    try
        // Not on allow-list, so dropped without counting toward stripped.
        Assert.DoesNotContain("KEYBOARD_LAYOUT=us", decodeEntries built)
        Assert.Equal(0, built.StrippedCount)
    finally freeBuilt built

[<Fact>]
let ``deny-list match is case-insensitive`` () =
    // Windows env-var names are case-insensitive by convention
    // (`%path%` and `%PATH%` are the same key). The deny-list must
    // catch a lowercase `github_token` the same as `GITHUB_TOKEN`.
    let parent = Map.ofList [ "github_token", "ghp_xxx" ]
    let built = EnvBlock.buildFromMap parent
    try
        Assert.DoesNotContain("github_token=ghp_xxx", decodeEntries built)
        Assert.DoesNotContain("GITHUB_TOKEN=ghp_xxx", decodeEntries built)
        Assert.Equal(1, built.StrippedCount)
    finally freeBuilt built

// ---------------------------------------------------------------------
// Always-set TERM / COLORTERM
// ---------------------------------------------------------------------

[<Fact>]
let ``TERM is always set to xterm-256color even if parent has TERM=xterm`` () =
    let parent = Map.ofList [ "TERM", "xterm" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built
        Assert.Contains("TERM=xterm-256color", entries)
        Assert.DoesNotContain("TERM=xterm", entries)
    finally freeBuilt built

[<Fact>]
let ``COLORTERM is always set to truecolor`` () =
    let built = EnvBlock.buildFromMap Map.empty
    try
        Assert.Contains("COLORTERM=truecolor", decodeEntries built)
    finally freeBuilt built

[<Fact>]
let ``empty parent still produces TERM and COLORTERM entries`` () =
    let built = EnvBlock.buildFromMap Map.empty
    try
        let entries = decodeEntries built |> List.sort
        Assert.Equal<string list>(
            [ "COLORTERM=truecolor"; "TERM=xterm-256color" ],
            entries)
    finally freeBuilt built

// ---------------------------------------------------------------------
// HOME=%USERPROFILE% fallback
// ---------------------------------------------------------------------

[<Fact>]
let ``HOME falls back to USERPROFILE when absent`` () =
    let parent = Map.ofList [ "USERPROFILE", "C:\\Users\\test" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built
        Assert.Contains("HOME=C:\\Users\\test", entries)
        Assert.Contains("USERPROFILE=C:\\Users\\test", entries)
    finally freeBuilt built

[<Fact>]
let ``HOME is preserved when set even if USERPROFILE differs`` () =
    let parent =
        Map.ofList
            [ "HOME", "C:\\custom-home"
              "USERPROFILE", "C:\\Users\\test" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built
        Assert.Contains("HOME=C:\\custom-home", entries)
        Assert.Contains("USERPROFILE=C:\\Users\\test", entries)
    finally freeBuilt built

[<Fact>]
let ``HOME is absent when neither HOME nor USERPROFILE is set`` () =
    // Uses `List.tryFind` + `Assert.Equal` instead of
    // `Assert.DoesNotContain(collection, predicate)` because F# 9
    // doesn't auto-convert lambdas to xUnit's legacy
    // `Predicate<T>` delegate (only `System.Func` / `System.Action`
    // get implicit conversion).
    let built = EnvBlock.buildFromMap Map.empty
    try
        let entries = decodeEntries built
        let homeEntry =
            entries
            |> List.tryFind (fun (e: string) ->
                e.StartsWith("HOME=", StringComparison.Ordinal))
        Assert.Equal<string option>(None, homeEntry)
    finally freeBuilt built

// ---------------------------------------------------------------------
// Marshalling round-trip — the silent-failure canary
// ---------------------------------------------------------------------

[<Fact>]
let ``marshalled bytes are UTF-16LE NAME=VALUE\u0000 sorted by uppercase name`` () =
    // Construct a deterministic block from a synthetic parent that
    // produces exactly two entries in the final block (TERM,
    // COLORTERM, plus PATH from the parent → three entries) so the
    // sort order, separator, and terminator can be asserted at byte
    // level without ambiguity.
    let parent = Map.ofList [ "PATH", "C:\\W" ]
    let built = EnvBlock.buildFromMap parent
    try
        // Final block, sorted by uppercase name:
        //   COLORTERM=truecolor\u0000
        //   PATH=C:\W\u0000
        //   TERM=xterm-256color\u0000
        //   \u0000  (block terminator)
        let expectedString =
            "COLORTERM=truecolor\u0000"
            + "PATH=C:\\W\u0000"
            + "TERM=xterm-256color\u0000"
            + "\u0000"
        let expected = Encoding.Unicode.GetBytes(expectedString)
        let actual = readBlock built
        Assert.Equal<byte[]>(expected, actual)
        // Also verify the ByteLength matches the buffer's true size.
        Assert.Equal(expected.Length, built.ByteLength)
    finally freeBuilt built

[<Fact>]
let ``marshalled bytes use UTF-16LE encoding (low byte first for ASCII)`` () =
    // Catches the failure mode where `Encoding.BigEndianUnicode` (or
    // ASCII / UTF-8) is used instead of `Encoding.Unicode`. With
    // little-endian UTF-16, the byte for 'A' (0x41) is followed by
    // 0x00; with big-endian, 0x00 comes first.
    let built = EnvBlock.buildFromMap Map.empty
    try
        let bytes = readBlock built
        // First entry is "COLORTERM=truecolor\u0000".
        // First byte should be 'C' = 0x43, second byte 0x00.
        Assert.Equal(0x43uy, bytes.[0])
        Assert.Equal(0x00uy, bytes.[1])
        // Second char 'O' = 0x4F.
        Assert.Equal(0x4Fuy, bytes.[2])
        Assert.Equal(0x00uy, bytes.[3])
    finally freeBuilt built

[<Fact>]
let ``empty input produces a 2-byte block holding the block terminator`` () =
    // Edge case: every parent var is filtered out AND the always-set
    // pairs are themselves filtered (impossible in practice, but the
    // pure marshalBlock helper should handle empty input). Goes
    // through the internal helper to bypass the always-set layer.
    let ptr, length = EnvBlock.marshalBlock []
    try
        Assert.Equal(2, length)
        let bytes = Array.zeroCreate<byte> length
        Marshal.Copy(ptr, bytes, 0, length)
        Assert.Equal<byte[]>([| 0x00uy; 0x00uy |], bytes)
    finally Marshal.FreeHGlobal(ptr)

// ---------------------------------------------------------------------
// Stripped-count semantics
// ---------------------------------------------------------------------

[<Fact>]
let ``StrippedCount counts deny-list strips, not allow-list misses`` () =
    let parent =
        Map.ofList
            [ "GITHUB_TOKEN", "ghp_xxx"      // stripped by deny-list
              "EDITOR", "vim"                // dropped (not on allow-list)
              "PATH", "C:\\Windows" ]        // preserved
    let built = EnvBlock.buildFromMap parent
    try
        // EDITOR is dropped silently (not sensitive). Only
        // GITHUB_TOKEN counts.
        Assert.Equal(1, built.StrippedCount)
    finally freeBuilt built

// ---------------------------------------------------------------------
// Direct internal-helper coverage (via InternalsVisibleTo)
// ---------------------------------------------------------------------

[<Fact>]
let ``isDenied catches the four pattern families`` () =
    // Direct introspection of the deny-list predicate so a future
    // refactor can't silently change the matching rule (e.g.
    // accidentally substring-match instead of suffix-match).
    Assert.True(EnvBlock.isDenied "GITHUB_TOKEN")
    Assert.True(EnvBlock.isDenied "OPENAI_SECRET")
    Assert.True(EnvBlock.isDenied "BANK_API_KEY")
    Assert.True(EnvBlock.isDenied "DB_PASSWORD")

[<Fact>]
let ``isDenied does not match ANTHROPIC_API_KEY (Claude exemption)`` () =
    Assert.False(EnvBlock.isDenied "ANTHROPIC_API_KEY")
    Assert.False(EnvBlock.isDenied "anthropic_api_key")

[<Fact>]
let ``isDenied does not match non-suffix occurrences`` () =
    Assert.False(EnvBlock.isDenied "KEYBOARD_LAYOUT")
    Assert.False(EnvBlock.isDenied "TOKENISER")
    Assert.False(EnvBlock.isDenied "PASSWORD_MANAGER_PATH")
    Assert.False(EnvBlock.isDenied "SECRET_SAUCE_RECIPE")

[<Fact>]
let ``allowedNames contains exactly the spec-7-2 baseline`` () =
    // Pinning the allow-list set protects against accidental
    // additions that would broaden the env-leak surface beyond
    // what `spec/tech-plan.md` §7.2 authorises. Adding a name
    // requires a spec PR + this assertion update — the same
    // ADR-style discipline the chat-2026-05-03 stage-numbering
    // chunk used.
    //
    // PR-K (2026-05-03 maintainer authorisation) added the
    // Windows-baseline runtime vars (SystemRoot, WINDIR, TEMP,
    // ProgramFiles, …) after the empirical NVDA pass surfaced
    // that PowerShell + claude.exe both die immediately on spawn
    // because the original 7-name allow-list was too narrow for
    // any non-cmd shell to initialise.
    let expected =
        Set.ofList
            [ // Layer 1 — pty-speak-specific
              "PATH"; "USERPROFILE"; "APPDATA"
              "LOCALAPPDATA"; "HOME"
              "ANTHROPIC_API_KEY"; "CLAUDE_CODE_GIT_BASH_PATH"
              // Layer 2 — Windows runtime baseline (PR-K)
              "SystemRoot"; "WINDIR"; "SystemDrive"
              "TEMP"; "TMP"
              "ProgramFiles"; "ProgramFiles(x86)"; "ProgramW6432"
              "ProgramData"; "ALLUSERSPROFILE"; "PUBLIC"
              "PATHEXT"; "PSModulePath"
              "COMPUTERNAME"; "USERNAME"; "USERDOMAIN"
              "USERDOMAIN_ROAMINGPROFILE"
              "PROCESSOR_ARCHITECTURE"; "PROCESSOR_IDENTIFIER"
              "PROCESSOR_LEVEL"; "PROCESSOR_REVISION"
              "NUMBER_OF_PROCESSORS"; "OS"
              "LOGONSERVER"; "SESSIONNAME"
              "HOMEDRIVE"; "HOMEPATH"
              "DriverData" ]
    Assert.Equal<Set<string>>(expected, EnvBlock.allowedNames)

[<Fact>]
let ``allow-list preserves Windows runtime baseline vars (PR-K)`` () =
    // The empirical NVDA pass on 2026-05-03 surfaced that
    // PowerShell + claude.exe died immediately on spawn while
    // cmd.exe survived; root cause was the original allow-list
    // stripping these standard Windows runtime vars. This
    // fixture pins the layer-2 names so a future "tighten the
    // allow-list" change can't silently re-break PowerShell +
    // Node-based shells.
    let parent =
        Map.ofList
            [ "SystemRoot", "C:\\Windows"
              "WINDIR", "C:\\Windows"
              "SystemDrive", "C:"
              "TEMP", "C:\\Users\\test\\AppData\\Local\\Temp"
              "TMP", "C:\\Users\\test\\AppData\\Local\\Temp"
              "ProgramFiles", "C:\\Program Files"
              "ProgramFiles(x86)", "C:\\Program Files (x86)"
              "ProgramW6432", "C:\\Program Files"
              "ProgramData", "C:\\ProgramData"
              "ALLUSERSPROFILE", "C:\\ProgramData"
              "PUBLIC", "C:\\Users\\Public"
              "PATHEXT", ".COM;.EXE;.BAT"
              "PSModulePath", "C:\\Modules"
              "COMPUTERNAME", "TEST-PC"
              "USERNAME", "test"
              "USERDOMAIN", "TEST-PC"
              "PROCESSOR_ARCHITECTURE", "AMD64"
              "NUMBER_OF_PROCESSORS", "8"
              "OS", "Windows_NT"
              "HOMEDRIVE", "C:"
              "HOMEPATH", "\\Users\\test" ]
    let built = EnvBlock.buildFromMap parent
    try
        let entries = decodeEntries built |> Set.ofList
        // All of the above must survive the allow-list filter.
        // The block uppercases entry names internally, so assert
        // on uppercase keys.
        Assert.Contains("SYSTEMROOT=C:\\Windows", entries)
        Assert.Contains("WINDIR=C:\\Windows", entries)
        Assert.Contains("SYSTEMDRIVE=C:", entries)
        Assert.Contains(
            "TEMP=C:\\Users\\test\\AppData\\Local\\Temp",
            entries)
        Assert.Contains(
            "TMP=C:\\Users\\test\\AppData\\Local\\Temp",
            entries)
        Assert.Contains("PROGRAMFILES=C:\\Program Files", entries)
        Assert.Contains(
            "PROGRAMFILES(X86)=C:\\Program Files (x86)",
            entries)
        Assert.Contains("PROGRAMDATA=C:\\ProgramData", entries)
        Assert.Contains("PATHEXT=.COM;.EXE;.BAT", entries)
        Assert.Contains("PSMODULEPATH=C:\\Modules", entries)
        Assert.Contains("USERNAME=test", entries)
        Assert.Contains("PROCESSOR_ARCHITECTURE=AMD64", entries)
        Assert.Contains("OS=Windows_NT", entries)
    finally freeBuilt built

[<Fact>]
let ``ParentCount and KeptCount track the full filter picture (PR-K)`` () =
    // PR-K added these counts so the composition-root log line
    // can report "kept K of M parent vars; dropped D as
    // sensitive" instead of just the deny-list count. The
    // 2026-05-03 NVDA pass surfaced that the prior log line
    // "Env-scrub: stripped 0 variables" was misleading — it only
    // counted deny-list strikes, hiding the much larger silent
    // drop count from the allow-list filter.
    let parent =
        Map.ofList
            [ "PATH", "C:\\Windows"            // kept (allow-list)
              "USERPROFILE", "C:\\Users\\test" // kept (allow-list)
              "EDITOR", "vim"                   // dropped silently
              "PAGER", "less"                   // dropped silently
              "MY_RANDOM", "1"                  // dropped silently
              "GITHUB_TOKEN", "ghp_xxx" ]       // dropped (deny-list)
    let built = EnvBlock.buildFromMap parent
    try
        Assert.Equal(6, built.ParentCount)
        Assert.Equal(2, built.KeptCount)
        Assert.Equal(1, built.StrippedCount)
        // Sanity: the missing 3 are the silent drops (EDITOR,
        // PAGER, MY_RANDOM). ParentCount - KeptCount -
        // StrippedCount tells the user what fell into the
        // "outside allow-list" bucket.
    finally freeBuilt built

[<Fact>]
let ``KeptCount excludes always-set additions and HOME fallback (PR-K)`` () =
    // KeptCount tracks PARENT vars that survived; it doesn't
    // include the always-set TERM/COLORTERM pair (which would
    // inflate the count regardless of input) or the HOME=
    // %USERPROFILE% fallback (which is a synthesised entry, not
    // a parent var). This fixture pins that boundary.
    let parent = Map.ofList [ "USERPROFILE", "C:\\Users\\test" ]
    let built = EnvBlock.buildFromMap parent
    try
        Assert.Equal(1, built.ParentCount)
        Assert.Equal(1, built.KeptCount)
        // But the final block has 4 entries: USERPROFILE, HOME
        // (fallback), TERM, COLORTERM.
        let entries = decodeEntries built
        Assert.Equal(4, entries.Length)
    finally freeBuilt built
