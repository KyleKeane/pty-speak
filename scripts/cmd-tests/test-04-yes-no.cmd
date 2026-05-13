@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 04 — yes / no choice
REM
REM Exercises:
REM   * cmd's `choice /c YN` primitive — single-keystroke prompt
REM     (no Enter required). Different shape than `set /p`:
REM     the user presses Y or N and the script branches.
REM   * Tests whether NVDA hears the "Y, N?" prompt cmd emits
REM     before the user is expected to press a key.
REM
REM Pass criterion (NVDA): "Continue? Y, N?" reads aloud; user
REM presses Y or N (no Enter); branch result is narrated.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-04-yes-no ===
echo This test asks a yes/no question. Press Y or N (no Enter needed).
choice /c YN /m "Continue?"
if errorlevel 2 (
    echo You chose No.
) else (
    echo You chose Yes.
)
echo === PTYSPEAK-TEST-END: test-04-yes-no ===
