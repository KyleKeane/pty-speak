@echo off
REM ---------------------------------------------------------------
REM Cycle 47 CMD interaction test 01 — simple echo
REM
REM Exercises:
REM   * Basic multi-line stdout narration through ContentHistory.
REM   * Output-announce cap (Cycle 46 post-audit). The body is
REM     well under the 800-char cap so the auto-narrate reads
REM     all of it.
REM
REM Pass criterion (NVDA): the intro line, all three numbered
REM lines, and the final message audible end-to-end; ready-prompt
REM click fires once at the end.
REM
REM Cycle 49 (2026-05-14): script reshaped per maintainer
REM feedback — pre-Cycle-49 the script printed lines 2-7 with
REM "Line N of 8" labels but the implicit Line 1 and Line 8
REM (the intro line "This is a simple echo test." and the
REM final "Last line ..." message) carried no numeric label,
REM making "did I hear Line 1 of 8?" hard to answer.
REM ---------------------------------------------------------------
echo === PTYSPEAK-TEST-START: test-01-echo ===
echo Echo test follows: three numbered lines then a final message.
echo Line 1 of 3.
echo Line 2 of 3.
echo Line 3 of 3.
echo If you heard the intro, all three numbered lines, and this final message, output narration is healthy.
echo === PTYSPEAK-TEST-END: test-01-echo ===
