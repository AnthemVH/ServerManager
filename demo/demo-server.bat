@echo off
REM A stand-in game server for trying out Server Launcher.
REM Prints startup lines, then ticks once a second and responds to console commands.
REM Type "stop" in the console box (or use the Stop button) to shut it down cleanly.
setlocal enabledelayedexpansion

echo [INFO] Demo Server 1.0 starting...
echo [INFO] Loading world 'overworld'
echo [INFO] Loading world 'nether'
echo [INFO] Binding to port 25565
echo [INFO] Done. Server is ready for players.

set /a tick=0
:loop
set "line="
set /p line=
if defined line (
  if /i "!line!"=="stop" (
    echo [INFO] Shutdown requested, saving worlds...
    echo [INFO] Saved. Goodbye.
    exit /b 0
  )
  if /i "!line!"=="players" (
    echo [INFO] There are 0 of a max 20 players online.
  ) else (
    echo [INFO] Unknown command: !line!
  )
)
set /a tick+=1
echo [TICK] Uptime !tick! commands processed
goto loop
