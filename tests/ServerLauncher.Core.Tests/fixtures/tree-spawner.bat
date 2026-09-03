@echo off
REM Spawns a detached child, then idles. The critical orphan test: killing only the
REM cmd.exe we hold would leave the ping child running forever.
echo SPAWNER: starting child
start "" /b cmd /c "ping -t 127.0.0.1 > nul"
echo SPAWNER: child started
:loop
ping -n 2 127.0.0.1 > nul
goto loop
