@echo off
REM ---------------------------------------------------------------
REM Cycle 52 R3c CMD interaction test 09 — multi-interrupt
REM watermark-composition check
REM
REM Exercises:
REM   * THREE distinct output sections separated by TWO yes/no
REM     interruptions (`choice /c YN`). This is the composition
REM     case the R3c spoken-watermark must get right: each
REM     section is announced once, the question is announced
REM     once in real-time when it appears, and NOTHING is
REM     re-read at the next watermark advance.
REM
REM Pass criterion (NVDA), in this exact sequence, each spoken
REM exactly ONCE, no re-reads of an earlier segment:
REM   1. "SECTION ONE ..."  (before any prompt)
REM   2. "INTERRUPTION ONE: press Y or N"  (real-time)
REM   3. (you press Y or N)
REM   4. "SECTION TWO ..."  (ONLY section two — not a re-read
REM      of section one or interruption one)
REM   5. "INTERRUPTION TWO: press Y or N"  (real-time)
REM   6. (you press Y or N)
REM   7. "SECTION THREE ..."  (ONLY section three)
REM Fail signals: section one/two or an interruption is heard
REM a second time after a later answer (the watermark did not
REM advance / a string strip regressed).
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-09-multi-interrupt ===
echo SECTION ONE: first segment, spoken before any prompt. Listen for this once.
choice /c YN /m "INTERRUPTION ONE: press Y or N"
if errorlevel 2 (echo You answered No to interruption one.) else (echo You answered Yes to interruption one.)
echo SECTION TWO: second segment. This must be spoken alone after your first answer, NOT a re-read of section one or interruption one.
choice /c YN /m "INTERRUPTION TWO: press Y or N"
if errorlevel 2 (echo You answered No to interruption two.) else (echo You answered Yes to interruption two.)
echo SECTION THREE: final segment, spoken alone after your second answer.
echo === PTYSPEAK-TEST-END: test-09-multi-interrupt ===
