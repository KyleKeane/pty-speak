# Process-cleanup baseline test for pty-speak.
#
# Run from PowerShell. The script:
#   1. Locates the installed pty-speak under %LocalAppData%\pty-speak\.
#   2. For each close method (Alt+F4 then X button):
#        a. Launches the app.
#        b. Waits ~3 seconds for it to be running.
#        c. Reports the running-process state.
#        d. Tells you to close the window via the named method.
#        e. Polls every 500 ms until the main process exits
#           (no need to switch back to PowerShell and press Enter).
#        f. Waits a couple seconds for child processes to also exit.
#        g. Reports whether anything was left over.
#   3. Prints a clear PASS / FAIL summary at the end.
#
# Output is plain text on stdout, one fact per line, designed for
# screen-reader users to follow along audibly. No GUI.
#
# Designed by audit-cycle SR-2 + 2026-05-01 strategic review (PR #79)
# as the ACCESSIBILITY-TESTING.md "Lifecycle inflection points"
# helper, replacing the manual Task Manager walkthrough that was
# inaccessible.
#
# Run as:
#   PowerShell -ExecutionPolicy Bypass -File scripts\test-process-cleanup.ps1
#
# Or directly from a release tag (no local clone needed) with:
#   iex (iwr https://raw.githubusercontent.com/KyleKeane/pty-speak/main/scripts/test-process-cleanup.ps1 -UseBasicParsing).Content

$ErrorActionPreference = 'Stop'

# Velopack installs to %LocalAppData%\pty-speak\current\Terminal.App.exe
function Find-PtySpeakInstall {
    $candidates = @(
        "$env:LOCALAPPDATA\pty-speak\current\Terminal.App.exe",
        "$env:LOCALAPPDATA\pty-speak\Terminal.App.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

function Get-PtySpeakProcesses {
    $main = @(Get-Process Terminal.App -ErrorAction SilentlyContinue)
    $children = @()
    foreach ($parent in $main) {
        try {
            $kids = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id)" -ErrorAction SilentlyContinue
            if ($kids) { $children += $kids }
        } catch {
            # CIM query can occasionally fail mid-exit; treat as no children.
        }
    }
    return [PSCustomObject]@{
        Main      = $main
        Children  = $children
    }
}

# PR-J — enumerate live shell-related processes by NAME, not just
# by parent-PID. Catches sibling instances (a separate cmd window
# the user opened, an orphaned claude.exe from a previous crashed
# pty-speak session) that the parent-PID query misses. Mirrors the
# F# `enumerateShellProcesses` helper in
# `src/Terminal.App/Program.fs` so the inline Ctrl+Shift+D announce
# and the script's report use the same vocabulary.
function Get-ShellProcessSnapshot {
    $names = @('cmd', 'powershell', 'pwsh', 'claude', 'Terminal.App')
    $parts = @()
    foreach ($n in $names) {
        $procs = @(Get-Process -Name $n -ErrorAction SilentlyContinue)
        $parts += "$($procs.Count) $n"
    }
    return ($parts -join ', ')
}

function Wait-ForExit {
    param(
        [int]$TimeoutSeconds = 60
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $running = @(Get-Process Terminal.App -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Run-Pass {
    param(
        [string]$CloseMethod,
        [string]$ExePath
    )
    Write-Host ""
    Write-Host "----- Pass: close via $CloseMethod -----"
    Write-Host ""

    # If pty-speak is already running (the typical case when this
    # script is launched from inside the app via Ctrl+Shift+D), skip
    # the launch step and use the existing instance. Otherwise launch
    # a fresh one.
    $existing = Get-PtySpeakProcesses
    if ($existing.Main.Count -gt 0) {
        Write-Host "pty-speak is already running ($($existing.Main.Count) instance) - using current."
    } else {
        Write-Host "Launching pty-speak..."
        Start-Process -FilePath $ExePath
        Start-Sleep -Seconds 3
    }

    $running = Get-PtySpeakProcesses
    Write-Host "Process state before close:"
    Write-Host "    Terminal.App.exe instances: $($running.Main.Count)"
    if ($running.Children) {
        $names = ($running.Children | ForEach-Object { $_.Name }) -join ', '
        Write-Host "    Child process count: $($running.Children.Count) ($names)"
    } else {
        Write-Host "    Child process count: 0"
    }
    Write-Host "    Sibling snapshot: $(Get-ShellProcessSnapshot)"

    if ($running.Main.Count -eq 0) {
        Write-Host "FAIL: pty-speak failed to start. Aborting this pass."
        return @{ Pass = $false; Reason = "did not start" }
    }
    if ($running.Children.Count -eq 0) {
        Write-Host "WARNING: no ConPTY child detected. Stage 1 normally spawns cmd.exe."
        Write-Host "         Continuing the cleanup test anyway."
    }

    Write-Host ""
    Write-Host "ACTION NEEDED: switch to the pty-speak window and close it via $CloseMethod."
    Write-Host "               Script will detect the close automatically and continue."
    Write-Host ""

    $exited = Wait-ForExit -TimeoutSeconds 120
    if (-not $exited) {
        Write-Host "TIMEOUT: pty-speak still running after 2 minutes. Skipping this pass."
        return @{ Pass = $false; Reason = "timeout waiting for close" }
    }

    Write-Host "Detected pty-speak exit. Waiting 3 seconds for child processes to clean up..."
    Start-Sleep -Seconds 3

    $after = Get-PtySpeakProcesses
    Write-Host "Process state after close:"
    Write-Host "    Terminal.App.exe instances: $($after.Main.Count)"
    Write-Host "    Child process count: $($after.Children.Count)"
    Write-Host "    Sibling snapshot: $(Get-ShellProcessSnapshot)"

    if ($after.Main.Count -eq 0 -and $after.Children.Count -eq 0) {
        Write-Host "RESULT: PASS"
        return @{ Pass = $true; Reason = "clean" }
    } else {
        $childNames = ($after.Children | ForEach-Object { $_.Name }) -join ', '
        Write-Host "RESULT: FAIL"
        Write-Host "        Orphan main: $($after.Main.Count)"
        Write-Host "        Orphan children: $($after.Children.Count) ($childNames)"
        return @{ Pass = $false; Reason = "orphans remain" }
    }
}

# ----- Begin -----

Write-Host ""
Write-Host "==========================================="
Write-Host "  pty-speak process-cleanup baseline test"
Write-Host "==========================================="
Write-Host ""

# PR-J — print the shell-process snapshot up front so a
# screen-reader user (or a Claude session triaging on their
# behalf) gets the "what's currently running" answer in one
# scrollback page WITHOUT having to run the full close-and-recheck
# flow. The two passes below still cover that flow for orphan
# detection.
Write-Host "Shell process snapshot (sibling counts by name):"
Write-Host "    $(Get-ShellProcessSnapshot)"
Write-Host ""

$exePath = Find-PtySpeakInstall
if (-not $exePath) {
    Write-Host "ERROR: Could not find pty-speak install."
    Write-Host "       Looked in: %LocalAppData%\pty-speak\current\ and %LocalAppData%\pty-speak\."
    Write-Host "       Install pty-speak first, then re-run this script."
    exit 1
}
Write-Host "Found pty-speak at: $exePath"
Write-Host ""
Write-Host "This test runs two passes."
Write-Host ""
Write-Host "Pass 1 - Alt+F4 close:"
Write-Host "  If pty-speak is already running (typical when launched"
Write-Host "  via Ctrl+Shift+D), the test uses that instance."
Write-Host "  Otherwise the script launches one for you."
Write-Host "  When prompted, switch to pty-speak and press Alt+F4."
Write-Host ""
Write-Host "Pass 2 - X button close:"
Write-Host "  Pass 1 will have closed pty-speak; this pass launches"
Write-Host "  a fresh instance for you. When prompted, switch to"
Write-Host "  pty-speak and click the X button on the title bar."
Write-Host "  (If clicking is awkward, Alt+Space then Enter on"
Write-Host "  'Close' is keyboard-equivalent to the X button.)"
Write-Host ""
Write-Host "After each close the script auto-detects exit and"
Write-Host "continues without you needing to switch back."
Write-Host ""

$results = @{}

foreach ($method in 'Alt+F4', 'X button') {
    $r = Run-Pass -CloseMethod $method -ExePath $exePath
    $results[$method] = $r
}

Write-Host ""
Write-Host "==========================================="
Write-Host "  Final summary"
Write-Host "==========================================="
foreach ($method in 'Alt+F4', 'X button') {
    $r = $results[$method]
    if ($r.Pass) {
        Write-Host "$method close: PASS"
    } else {
        Write-Host "$method close: FAIL ($($r.Reason))"
    }
}
Write-Host ""
$allPass = ($results.Values | Where-Object { -not $_.Pass } | Measure-Object).Count -eq 0
if ($allPass) {
    Write-Host "OVERALL: PASS - no orphan processes detected on either close path."
    exit 0
} else {
    Write-Host "OVERALL: FAIL - see per-pass detail above."
    exit 2
}
