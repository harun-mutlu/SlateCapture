@echo off
chcp 65001 >nul
title SlateCapture - setup
cd /d "%~dp0"
if not exist logs mkdir logs
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { & '%~dp0scripts\setup.ps1' %* *>&1 | Tee-Object -FilePath 'logs\setup.log'; exit $LASTEXITCODE }"
echo.
pause
