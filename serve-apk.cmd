@echo off
chcp 65001 >nul
title SlateCapture - serve-apk
cd /d "%~dp0"
if not exist logs mkdir logs
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { & '%~dp0scripts\serve-apk.ps1' %* *>&1 | Tee-Object -FilePath 'logs\serve-apk.log'; exit $LASTEXITCODE }"
echo.
pause
