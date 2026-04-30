# Downloads and installs a pty-speak preview release.
#
# Use case: iterating on Stage 4+ preview builds, where the
# normal "open the GitHub Release page, navigate to the asset
# list, find Setup.exe, click 'Run anyway' through SmartScreen"
# flow is several screen-reader steps per iteration. This
# script collapses that into one command and strips the
# Mark-of-the-Web (MOTW) tag so SmartScreen doesn't prompt at
# all.
#
# Examples:
#   .\install-latest-preview.ps1
#   .\install-latest-preview.ps1 -Tag v0.0.1-preview.22
#
# Notes:
#   * `Unblock-File` removes the alternate data stream
#     (Zone.Identifier) that Windows attaches to internet
#     downloads. SmartScreen uses that stream to decide
#     whether to prompt; once it's gone the unsigned
#     installer launches without the "Windows protected your
#     PC" dialog. (Windows Defender's reputation system can
#     still prompt for genuinely-untrusted executables but
#     in practice this script handles the unsigned-preview
#     case cleanly.)
#   * Velopack installers are per-user and write to
#     `%LocalAppData%\pty-speak\`. No admin / UAC prompt is
#     expected. If one appears, the installer is targeting
#     `Program Files` — that'd be a packaging regression to
#     file.
#   * Once Stage 11 (auto-update) ships, this script becomes
#     unnecessary for in-place updates: `Ctrl+Shift+U` from
#     inside the running app will fetch a ~KB-sized delta
#     and restart in ~2 seconds.

[CmdletBinding()]
param(
    # Specific release tag to install (e.g. `v0.0.1-preview.22`).
    # Omit to install the most recently published release.
    [string]$Tag = "",

    # Repository to pull from. Override only if you've forked.
    [string]$Repo = "KyleKeane/pty-speak"
)

$ErrorActionPreference = "Stop"

if (-not $Tag) {
    Write-Host "Querying latest release tag for $Repo..."
    $latest = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=1"
    if (-not $latest -or $latest.Count -eq 0) {
        throw "No releases found for $Repo."
    }
    $Tag = $latest[0].tag_name
}

Write-Host "Tag: $Tag"

$assetName = "pty-speak-win-Setup.exe"
$url = "https://github.com/$Repo/releases/download/$Tag/$assetName"
$path = Join-Path $env:TEMP $assetName

Write-Host "Downloading $assetName to $path..."
Invoke-WebRequest -Uri $url -OutFile $path

if (-not (Test-Path $path)) {
    throw "Download appeared to succeed but file is missing at $path."
}

$bytes = (Get-Item $path).Length
if ($bytes -lt 50000000) {
    Write-Warning "Setup.exe is only $bytes bytes (expected ~60-70 MB for a self-contained .NET 9 publish). The download may be truncated; consider re-running."
}

Write-Host "Removing Mark-of-the-Web so SmartScreen doesn't prompt..."
Unblock-File $path

Write-Host "Running installer..."
Start-Process $path -Wait

Write-Host "Done. Launch pty-speak from the Start menu."
