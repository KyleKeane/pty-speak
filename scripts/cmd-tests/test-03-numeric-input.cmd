@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 03 — numeric input + calculation
REM
REM Exercises:
REM   * `set /p` numeric prompt + `set /a` arithmetic. The
REM     maintainer's exact requested example: "prompt me for a
REM     number and then return 1+ that number".
REM   * If the user types non-numeric text, `set /a` errors —
REM     useful to hear how stderr / "Invalid number" messages
REM     surface.
REM
REM Pass criterion (NVDA): the prompt is announced; user types a
REM number + Enter; "<n> + 1 = <n+1>" reads back cleanly.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-03-numeric-input ===
echo This test prompts for a number and reports its successor.
set /p num=Enter a number:
set /a result=%num%+1
echo %num% + 1 = %result%
echo === PTYSPEAK-TEST-END: test-03-numeric-input ===
