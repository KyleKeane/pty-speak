@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 06 — pause / continue
REM
REM Exercises:
REM   * cmd's `pause` builtin (prints "Press any key to continue . . ."
REM     and waits for a keystroke).
REM   * Tests an intermediate-stop interaction: there's output
REM     before the pause, the user has to press any key, then
REM     more output continues. Useful for hearing whether the
REM     "Press any key..." prompt is announced and whether the
REM     post-pause content narrates correctly.
REM
REM Pass criterion (NVDA): the first three lines narrate; the
REM pause prompt is announced; any keypress continues; the
REM final three lines narrate.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-06-pause ===
echo First section, line 1.
echo First section, line 2.
echo First section, line 3.
pause
echo Second section, line 1.
echo Second section, line 2.
echo Second section, line 3.
echo === PTYSPEAK-TEST-END: test-06-pause ===
