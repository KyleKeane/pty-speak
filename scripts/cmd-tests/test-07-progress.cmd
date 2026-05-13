@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 07 — progress loop
REM
REM Exercises:
REM   * Long-running script with periodic output. Uses `timeout`
REM     to spread the steps across a few seconds; each step
REM     prints "Step N of 5". Tests whether mid-stream narration
REM     keeps up under the `TupleFinalOnly` ShellPolicy default.
REM   * Without `LineByLine` set, the output reaches NVDA as one
REM     800-char-capped Announce at tuple finalise, not per-line
REM     during the loop. The script intentionally takes >5s so
REM     the maintainer can hear the difference.
REM
REM Pass criterion (NVDA): no audible feedback during the loop
REM (silent ~5s); on completion, all five "Step N of 5" lines
REM narrate together at tuple finalise.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-07-progress ===
echo Starting a 5-step progress loop with 1s between steps.
echo Step 1 of 5
timeout /t 1 /nobreak >nul
echo Step 2 of 5
timeout /t 1 /nobreak >nul
echo Step 3 of 5
timeout /t 1 /nobreak >nul
echo Step 4 of 5
timeout /t 1 /nobreak >nul
echo Step 5 of 5
echo Progress loop complete.
echo === PTYSPEAK-TEST-END: test-07-progress ===
