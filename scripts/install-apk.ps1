. "$PSScriptRoot\common.ps1"

$apk = Get-ChildItem $Dist -Filter 'SlateCapture-*.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw 'No APK found. Run build.cmd first.' }

Step "Installing $($apk.Name) over USB"
Run $Adb @('install', '-r', $apk.FullName)
Write-Host "`nInstalled." -ForegroundColor Green
