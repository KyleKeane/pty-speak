@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 08 — stderr output
REM
REM Exercises:
REM   * stdout vs stderr surfacing. Lines redirected to `>&2`
REM     go to the process's stderr stream. ConPTY merges
REM     stderr into the PTY output, so pty-speak's parser sees
REM     a unified stream — but the user can listen for whether
REM     errors are perceptually distinct from regular output.
REM   * Tests whether anything in pty-speak special-cases
REM     stderr-ish content (it doesn't today; the Cycle 8d.2
REM     colour-detection plan was reverted).
REM
REM Pass criterion (NVDA): all six lines audible; "Warning" and
REM "Error" lines mixed in with the regular lines (no audible
REM stream distinction since pty-speak doesn't separate them).
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-08-stderr ===
echo Regular line one.
echo Warning: this line is on stderr.>&2
echo Regular line two.
echo Error: this line is also on stderr.>&2
echo Regular line three.
echo Regular line four.
echo === PTYSPEAK-TEST-END: test-08-stderr ===
