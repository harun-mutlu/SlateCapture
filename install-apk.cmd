@echo off
chcp 65001 >nul
title SlateCapture - install-apk
cd /d "%~dp0"
if not exist logs mkdir logs
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { & '%~dp0scripts\install-apk.ps1' %* *>&1 | Tee-Object -FilePath 'logs\install-apk.log'; exit $LASTEXITCODE }"
echo.
pause
