@echo off
REM Emits a burst of numbered lines to exercise log capture and ring-buffer capping.
setlocal enabledelayedexpansion
set /a i=0
:loop
set /a i+=1
echo LINE !i!
if !i! GEQ 200 exit /b 0
goto loop
