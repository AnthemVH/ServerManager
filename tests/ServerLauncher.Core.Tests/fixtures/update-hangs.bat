@echo off
echo Starting a very long update
:loop
ping -n 2 127.0.0.1 >nul
goto loop
