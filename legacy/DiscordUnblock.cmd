@echo off
:: Double-click launcher: asks for admin rights, then opens the DiscordUnblock menu.
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0DiscordUnblock.ps1" %*
pause
