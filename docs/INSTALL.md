# Install pty-speak

End-user install instructions for `pty-speak`. If you want to build
from source instead of running a pre-built preview, see
[`docs/BUILD.md`](BUILD.md). For the full doc-ownership index, see
[`docs/DOC-MAP.md`](DOC-MAP.md).

## Status warning — preview builds are unsigned

`pty-speak` is currently shipping `v0.0.x-preview.N` releases, which
carry **no Authenticode signature and no Ed25519 manifest signature**.
Windows SmartScreen will warn on first install:

> *Windows protected your PC. Microsoft Defender SmartScreen prevented
> an unrecognized app from starting...*

To allow the install: choose **More info** → **Run anyway**. NVDA and
JAWS announce both controls; the **More info** link reveals the
**Run anyway** button which then becomes activatable.

**Do not install preview builds on machines that handle sensitive
data.** Authenticode + Ed25519 signing return before `v0.1.0` — see
[`SECURITY.md`](../SECURITY.md) for the threat-model context and the
release-signing roadmap.

## Download the latest preview

1. Visit the releases page:
   [https://github.com/KyleKeane/pty-speak/releases](https://github.com/KyleKeane/pty-speak/releases).
2. Find the most recent `v0.0.x-preview.N` entry at the top of the list.
3. Under **Assets**, download `pty-speak-Setup.exe`.

## Install

Run the downloaded `pty-speak-Setup.exe`. Velopack installs to
`%LocalAppData%\pty-speak\current\` and creates a Start menu entry.
**No admin / UAC prompt** — the installer is per-user, not machine-wide.

Launch from the Start menu. NVDA should announce the window and a
short orientation line on first run. Press `Alt+F1` for keyboard
help at any time.

## Update later

From inside `pty-speak`, press `Ctrl+Shift+U` to check GitHub
Releases for a newer preview and apply the delta. Updates land in
~2 seconds; NVDA narrates progress (`Checking for updates...` →
`Downloading...` → bucketed percent → `Restarting to apply update`).
The window title carries the version suffix, so the audible
version-flip on restart confirms the new preview is running.

If the update flow surfaces an error, the announcement is one of the
documented messages in [`docs/UPDATE-FAILURES.md`](UPDATE-FAILURES.md).

## Alternative: PowerShell helper

If you prefer a scripted install, the repository ships a PowerShell
helper at
[`scripts/install-latest-preview.ps1`](../scripts/install-latest-preview.ps1).
Run it from any PowerShell session and it downloads and installs the
latest preview without browser interaction.

See [`scripts/README.md`](../scripts/README.md) for the full helper
catalog and usage notes.

## What to do next

After install:

- The full set of app-reserved hotkeys (`Ctrl+Shift+U/D/R/L/;/G/H/B/1/2/3`)
  with NVDA-validated descriptions and reproduction notes lives in
  [`README.md`](../README.md) under **App-reserved hotkeys**.
- Settings that are currently hardcoded but candidate for future
  user-configuration are catalogued in
  [`docs/USER-SETTINGS.md`](USER-SETTINGS.md).
- For NVDA validation of any specific stage, see
  [`docs/ACCESSIBILITY-TESTING.md`](ACCESSIBILITY-TESTING.md).
- For the project's wider context — author bio, the workarounds
  Kyle uses to make this work in practice, and the values frame
  (WHO ICF) that drives the technical decisions — see
  [`docs/PROJECT-CONTEXT.md`](PROJECT-CONTEXT.md).

## Build from source

If you want to develop pty-speak rather than just use it,
[`docs/BUILD.md`](BUILD.md) covers the local `dotnet build` /
`dotnet test` recipe, .NET SDK version requirements, and the F# /
WPF tooling options.
