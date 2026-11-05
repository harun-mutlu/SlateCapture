@echo off
chcp 65001 >nul
title SlateCapture - build
cd /d "%~dp0"
if not exist logs mkdir logs
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { & '%~dp0scripts\build.ps1' %* *>&1 | Tee-Object -FilePath 'logs\build.log'; exit $LASTEXITCODE }"
echo.
pause
