# Build from source

## Prerequisites

- **Windows 10 1809 (build 17763) or newer.** ConPTY is the only supported PTY
  mechanism; older Windows builds will not work.
- **.NET 9 SDK.** Install with `winget install Microsoft.DotNet.SDK.9` or
  download from [dot.net](https://dotnet.microsoft.com/).
- **An F# / .NET editor.** Visual Studio 2022 (with the F# workload),
  JetBrains Rider, or VS Code with the Ionide extension.
- **NVDA** (free, [nvaccess.org](https://www.nvaccess.org/)) plus the
  [Event Tracker add-on](https://addons.nvda-project.org/addons/evtTracker.en.html)
  for verifying Notification events.
- **Velopack CLI** (`vpk`) — `dotnet tool install -g vpk`.
- *Optional:* Inspect.exe and AccEvent.exe from the
  [Windows SDK](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/),
  Accessibility Insights for Windows, FlaUI for automated UIA tests.

## Clone

```powershell
git clone https://github.com/KyleKeane/pty-speak.git
cd pty-speak
```

## Restore, build, test

```powershell
dotnet restore
dotnet build -c Release
dotnet test  -c Release --no-build
```

The solution targets `net9.0-windows`. The F# WPF entry project uses
`Microsoft.NET.Sdk` (not `Microsoft.NET.Sdk.WindowsDesktop`) with
`<UseWpf>true</UseWpf>`, `<OutputType>Exe</OutputType>`, and
`<DisableWinExeOutputInference>true</DisableWinExeOutputInference>`.
This is the standard pattern for an F# WPF app with a C# XAML library;
see Velopack issue #195 for why `VelopackApp.Build().Run()` must run
before any WPF type loads.

## Run locally

```powershell
dotnet run --project src/Terminal.App -c Debug
```

A WPF window opens. NVDA should announce the window title and the help
hint. Press `Alt+F1` for keyboard help (once Stage 4 lands).

## Pack a local Velopack installer

This builds an installer identical in shape to what CI produces, but
**unsigned**. Use it to test the install/update flow on your own
machine.

```powershell
dotnet publish src/Terminal.App/Terminal.App.fsproj -c Release -r win-x64 --self-contained -o publish
vpk pack `
    --packId pty-speak `
    --packTitle "pty-speak" `
    --packAuthors "pty-speak contributors" `
    --packVersion 0.0.1-local `
    --packDir publish `
    --mainExe Terminal.App.exe `
    --outputDir releases
```

`vpk pack` rejects `--packVersion 0.0.0`; use `0.0.1-local` (or any
SemVer value `>= 0.0.1`) for local smoke testing.

The output `releases/pty-speak-Setup.exe` installs to
`%LocalAppData%\pty-speak\current\`. To test the auto-update path
locally, increment `--packVersion`, repack, and host the `releases/`
folder over HTTP.

## Common build failures

| Symptom                                                    | Likely cause                                                                              |
|------------------------------------------------------------|-------------------------------------------------------------------------------------------|
| `error FS3217: This expression is not a function`          | F# WPF SDK reference missing — check `<UseWpf>true</UseWpf>` is set on the project.       |
| `0x57 ERROR_INVALID_PARAMETER` from `CreateProcess`        | `STARTUPINFOEX.cb` set to `sizeof<STARTUPINFO>` instead of `sizeof<STARTUPINFOEX>`.       |
| Hang on first ConPTY read                                  | Forgot to close `inputReadSide` and `outputWriteSide` in the parent after `CreateProcess`. |
| Velopack apply hits an infinite restart loop               | `VelopackApp.Build().Run()` not called as the very first thing in `main`.                  |
| `dotnet test` passes locally, fails in CI on UIA tests     | CI runner does not have an interactive desktop session — confirm `windows-latest`, not `windows-2019-azure`. |
| `error MC1002: ApplicationDefinition not allowed in Library projects` | A WPF `<UseWPF>true</UseWPF>` library project shipped an `App.xaml`. The SDK auto-classifies any `App.xaml` as `ApplicationDefinition`. Remove `App.xaml` and use a plain `App.cs : Application`, or move the entry to the executable project. |
| `error NETSDK1022: Duplicate 'Page' items` (or `ApplicationDefinition`) | Project-file has explicit `<Page>` / `<ApplicationDefinition>` items that duplicate the SDK's auto-glob. Remove them. |
| `error NETSDK1047: Assets file ... doesn't have a target for 'win-x64'` | Self-contained `dotnet publish -r win-x64` was passed `--no-restore` after a platform-default restore. Drop `--no-restore`, or restore with the RID. |
| `error CS0103: The name 'Background' does not exist in the current context` | Using `Background = ...` on a `FrameworkElement` subclass. `FrameworkElement` does not expose `Background` (only `Control` and `Panel` do). Add a private brush field. |
| `error FS0039: The value or constructor 'X' is not defined` *inside its own definition* | Self-recursive `let` binding inside a class body needs `let rec`, not `let`. The compiler does not suggest the fix. |
| `vpk pack` rejects `--packVersion 0.0.0`                   | Velopack's `--packVersion` requires `>= 0.0.1`. Use `0.0.1-local` for local smoke testing. |

## Continuous integration

CI runs on every push and PR via
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml):

1. **Workflow lint** (`actionlint`) — catches the YAML / shell /
   expression mistakes that produced the silent workflow
   `startup_failures` during the `v0.0.1-preview.{1..5}` diagnostic
   loop.
2. Restore.
3. Build `-c Release`.
4. Test `-c Release --no-build`.
5. Velopack `vpk pack` smoke (uploads the resulting installer as a
   `velopack-smoke-<run>` artifact, 7-day retention).
6. Upload test results as a workflow artifact.

The CI runner is `windows-latest` because UIA tests require an
interactive desktop. We do **not** install NVDA in CI — assertions
target the UIA producer level and stay screen-reader-agnostic. NVDA and
Narrator validation is manual per release; see
[`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).

## Releasing

Cutting a public release goes through the workflow described in
[`docs/RELEASE-PROCESS.md`](RELEASE-PROCESS.md). The short version:
push a `vX.Y.Z` tag and the release workflow builds, signs, packs, and
uploads to GitHub Releases.
