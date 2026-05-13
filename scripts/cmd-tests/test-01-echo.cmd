@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 01 — simple echo
REM
REM Exercises:
REM   * Basic multi-line stdout narration through ContentHistory.
REM   * Output-announce cap (Cycle 46 post-audit). This script
REM     emits ~10 lines; the body is well under the 800-char cap
REM     so the auto-narrate should read all of it.
REM
REM Pass criterion (NVDA): all numbered lines audible end-to-end;
REM ready-prompt click fires once at the end.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-01-echo ===
echo This is a simple echo test.
echo Line 2 of 8.
echo Line 3 of 8.
echo Line 4 of 8.
echo Line 5 of 8.
echo Line 6 of 8.
echo Line 7 of 8.
echo Last line. If you heard all eight numbered lines, output narration is healthy.
echo === PTYSPEAK-TEST-END: test-01-echo ===
