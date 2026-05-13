@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 02 — text input via `set /p`
REM
REM Exercises:
REM   * cmd's built-in line-edited prompt (`set /p`). User types a
REM     string + Enter; cmd captures it into a variable.
REM   * Tests whether pty-speak's input echo behaves usefully
REM     during a script-driven prompt (vs the shell prompt).
REM
REM Pass criterion (NVDA): the prompt "Enter your name:" is
REM announced; the user can type a name; the echoed greeting
REM is narrated after Enter.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-02-text-input ===
echo This test prompts for a text string and echoes it back.
set /p name=Enter your name:
echo Hello, %name%! Your name has %name:~0,1% as its first letter.
echo === PTYSPEAK-TEST-END: test-02-text-input ===
