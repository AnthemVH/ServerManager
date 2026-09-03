@echo off
REM Echoes stdin so the graceful-stop path can be verified end to end.
echo READY
:loop
set "line="
set /p line=
if not defined line goto done
if /i "%line%"=="stop" (
  echo STOPPING
  exit /b 0
)
echo ECHO: %line%
goto loop
:done
exit /b 0
