param([switch]$All)
. "$PSScriptRoot\common.ps1"

$version = Get-AppVersion
New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$apk = Join-Path $Dist "SlateCapture-$version.apk"

Step "Android app $version"
Push-Location (Join-Path $Root 'android')
try { Run (Join-Path (Get-Location) 'gradlew.bat') @(':app:assembleRelease', '--no-daemon', '--console=plain') }
finally { Pop-Location }
$built = Join-Path $Root 'android\app\build\outputs\apk\release\app-release.apk'
if (-not (Test-Path $built)) { throw 'APK was not produced' }
Copy-Item $built $apk -Force
Write-Host "  $apk"

$project = Join-Path $Root 'desktop\SlateCapture\SlateCapture.csproj'

function Publish([string]$Rid) {
    $out = Join-Path $Build $Rid
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Run $Dotnet @('publish', $project, '-c', 'Release', '-r', $Rid, '--self-contained', 'true', '-o', $out,
        '-p:DebugType=none', '-p:PublishReadyToRun=false') | Out-Host
    Copy-Item $apk (Join-Path $out 'SlateCapture.apk')
    Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $out 'LICENSE.txt')
    return $out
}

Step 'Desktop app (Windows x64)'
Get-Process -Name 'SlateCapture' -ErrorAction SilentlyContinue | Stop-Process -Force
$win = Publish 'win-x64'
$ffmpeg = Join-Path $win 'ffmpeg'
New-Item -ItemType Directory -Force -Path $ffmpeg | Out-Null
Copy-Item (Join-Path $Tools 'ffmpeg\win-x64\*') $ffmpeg -Recurse

Step 'Portable zip'
$portable = Join-Path $Build 'portable'
Remove-Item $portable -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $win $portable -Recurse
New-Item -ItemType File -Path (Join-Path $portable 'portable') | Out-Null
$zip = Join-Path $Dist "SlateCapture-$version-win-x64-portable.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "  $zip"

Step 'Installer'
Run $Iscc @("/DAppVersion=$version", '/Q', (Join-Path $Root 'installer\SlateCapture.iss'))
Write-Host "  $(Join-Path $Dist "SlateCapture-$version-setup.exe")"

if ($All) {
    foreach ($rid in 'linux-x64', 'osx-arm64', 'osx-x64') {
        Step "Desktop app ($rid)"
        $out = Publish $rid
        $archive = Join-Path $Dist "SlateCapture-$version-$rid.zip"
        Remove-Item $archive -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -CompressionLevel Optimal
        Write-Host "  $archive"
    }
}

Write-Host "`nBuild finished. Files are in $Dist" -ForegroundColor Green
