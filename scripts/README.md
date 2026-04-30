# scripts/

Repo-local utility scripts. Each is documented at the top of its
own file; this README is the index.

## `install-latest-preview.ps1`

PowerShell installer for the most recent (or specified) `pty-speak`
preview release on Windows. Bypasses the SmartScreen prompt the
unsigned-preview line currently triggers by stripping the
Mark-of-the-Web from the downloaded installer.

Use case: iterating on preview builds during Stage 4+ smoke
testing. Replaces the multi-step "open release page → navigate to
asset list → click `Setup.exe` → click 'More info' → click 'Run
anyway' → wait for installer" flow with one command.

```powershell
# Latest release
.\scripts\install-latest-preview.ps1

# Specific tag
.\scripts\install-latest-preview.ps1 -Tag v0.0.1-preview.22

# Different fork
.\scripts\install-latest-preview.ps1 -Repo someone/pty-speak-fork
```

The script prints each step (querying tag, downloading, unblocking,
running) to stdout — NVDA users can follow along by ear.

Once Stage 11 (auto-update via Velopack delta nupkgs) ships, this
script becomes unnecessary for in-place updates: pressing
`Ctrl+Shift+U` from inside the running app will fetch a
~KB-sized delta and restart automatically.
