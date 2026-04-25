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
`<DisableWinExeOutputInference>true</DisableWinExeOutputInference>` —
this is the standard Elmish.WPF setup.

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
dotnet publish src/Terminal.App -c Release -r win-x64 --self-contained -o publish
vpk pack `
    --packId pty-speak `
    --packTitle "pty-speak" `
    --packAuthors "pty-speak contributors" `
    --packVersion 0.0.0-local `
    --packDir publish `
    --mainExe Terminal.App.exe `
    --outputDir releases
```

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

## Continuous integration

CI runs on every push and PR via
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml):

1. Restore.
2. Build `-c Release`.
3. Test `-c Release --no-build`.
4. Upload test results as a workflow artifact.

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
