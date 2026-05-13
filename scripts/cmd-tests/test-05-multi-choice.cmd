@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 05 — multi-option choice
REM
REM Exercises:
REM   * cmd's `choice /c 1234` primitive with a four-way
REM     numbered selection. Tests how multi-option prompts
REM     surface — they're the closest cmd analogue to Claude's
REM     tool-use selection lists.
REM   * `/n` suppresses the default "[1,2,3,4]?" trailer so we
REM     can see whether NVDA picks up the prompt text alone.
REM
REM Pass criterion (NVDA): the four options are presented;
REM user presses 1-4; chosen branch narrates.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-05-multi-choice ===
echo This test offers four numbered choices.
echo   1: Coffee
echo   2: Tea
echo   3: Water
echo   4: Cancel
choice /c 1234 /n /m "Pick 1-4:"
if errorlevel 4 (echo You picked Cancel.) ^
else if errorlevel 3 (echo You picked Water.) ^
else if errorlevel 2 (echo You picked Tea.) ^
else (echo You picked Coffee.)
echo === PTYSPEAK-TEST-END: test-05-multi-choice ===
