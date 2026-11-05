. "$PSScriptRoot\common.ps1"

$downloads = Join-Path $Tools 'downloads'
New-Item -ItemType Directory -Force -Path $Tools, $downloads | Out-Null

function Expand-Into([string]$Zip, [string]$Target, [string]$Inner) {
    $tmp = Join-Path $downloads 'unpack'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $Zip $tmp
    $src = if ($Inner) { Join-Path $tmp $Inner } else { (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName }
    New-Item -ItemType Directory -Force -Path (Split-Path $Target) | Out-Null
    Move-Item $src $Target
    Remove-Item $tmp -Recurse -Force
}

Step 'JDK 17'
if (-not (Test-Path "$env:JAVA_HOME\bin\java.exe")) {
    $zip = Join-Path $downloads 'jdk17.zip'
    Download 'https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip' $zip
    Expand-Into $zip $env:JAVA_HOME
}
Write-Host '  ok'

Step 'Android SDK'
$sdkmanager = Join-Path $env:ANDROID_HOME 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-Path $sdkmanager)) {
    $zip = Join-Path $downloads 'cmdline-tools.zip'
    Download 'https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip' $zip
    Expand-Into $zip (Join-Path $env:ANDROID_HOME 'cmdline-tools\latest') 'cmdline-tools'
}
if (-not (Test-Path (Join-Path $env:ANDROID_HOME 'build-tools\35.0.0'))) {
    $licenses = Join-Path $env:ANDROID_HOME 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    "`n24333f8a63b6825ea9c5514f83c2829b004d1fee`nd56f5187479451eabf01fb78af6dfcb131a6481e" |
        Set-Content (Join-Path $licenses 'android-sdk-license') -Encoding ASCII -NoNewline
    "`n84831b9409646a918e30573bab4c9c91346d8abd" |
        Set-Content (Join-Path $licenses 'android-sdk-preview-license') -Encoding ASCII -NoNewline
    Run $sdkmanager @("--sdk_root=$env:ANDROID_HOME", 'platform-tools', 'platforms;android-35', 'build-tools;35.0.0') (1..30 | ForEach-Object { 'y' }) |
        Where-Object { $_ -notmatch '^\s*\[' }
}
Write-Host '  ok'

Step '.NET SDK 10'
if (-not (Test-Path $Dotnet)) {
    $script = Join-Path $downloads 'dotnet-install.ps1'
    Download 'https://dot.net/v1/dotnet-install.ps1' $script
    & $script -Channel 10.0 -InstallDir $env:DOTNET_ROOT -NoPath
}
Write-Host '  ok'

Step 'FFmpeg (LGPL, shared)'
$ffmpeg = Join-Path $Tools 'ffmpeg\win-x64'
if (-not (Test-Path (Join-Path $ffmpeg 'avcodec-61.dll'))) {
    $zip = Join-Path $downloads 'ffmpeg-win64.zip'
    if (-not (Test-Path $zip)) {
        $headers = @{ 'User-Agent' = 'SlateCapture-setup' }
        $pattern = '^ffmpeg-n7\.\d+.*-win64-lgpl-shared-7\.\d+\.zip$'
        $asset = $null
        foreach ($page in 1..3) {
            $releases = Invoke-RestMethod "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases?per_page=30&page=$page" -Headers $headers
            foreach ($release in $releases) {
                $asset = $release.assets | Where-Object { $_.name -match $pattern } | Sort-Object name -Descending | Select-Object -First 1
                if ($asset) { break }
            }
            if ($asset -or -not $releases) { break }
        }
        if (-not $asset) { throw 'No FFmpeg 7.x LGPL shared build found on BtbN/FFmpeg-Builds' }
        Write-Host "  $($asset.name)"
        Download $asset.browser_download_url $zip
    }
    $tmp = Join-Path $downloads 'ffmpeg-unpack'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    New-Item -ItemType Directory -Force -Path $ffmpeg | Out-Null
    Get-ChildItem $tmp -Recurse -Filter '*.dll' | Copy-Item -Destination $ffmpeg
    Get-ChildItem $tmp -Recurse -Filter 'LICENSE*' | Select-Object -First 1 | Copy-Item -Destination (Join-Path $ffmpeg 'LICENSE.txt')
    Remove-Item $tmp -Recurse -Force
}
Write-Host '  ok'

Step 'Inno Setup'
if (-not (Test-Path $Iscc)) {
    $pkg = Join-Path $downloads 'innosetup.zip'
    Download 'https://www.nuget.org/api/v2/package/Tools.InnoSetup' $pkg
    $tmp = Join-Path $downloads 'inno-unpack'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $pkg $tmp
    $found = Get-ChildItem $tmp -Recurse -Filter 'ISCC.exe' | Select-Object -First 1
    if (-not $found) { throw 'ISCC.exe not found in the Inno Setup package' }
    Move-Item $found.Directory.FullName (Join-Path $Tools 'innosetup')
    Remove-Item $tmp -Recurse -Force
}
Write-Host '  ok'

Step 'Android signing key'
$signing = Join-Path $Root 'signing'
$props = Join-Path $signing 'keystore.properties'
if (-not (Test-Path $props)) {
    New-Item -ItemType Directory -Force -Path $signing | Out-Null
    $pass = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    Run (Join-Path $env:JAVA_HOME 'bin\keytool.exe') @(
        '-genkeypair', '-v', '-keystore', (Join-Path $signing 'slatecapture.jks'),
        '-alias', 'slatecapture', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000',
        '-storepass', $pass, '-keypass', $pass, '-dname', 'CN=SlateCapture') | Out-Null
    @(
        'storeFile=slatecapture.jks'
        "storePassword=$pass"
        'keyAlias=slatecapture'
        "keyPassword=$pass"
    ) | Set-Content $props -Encoding ASCII
    Write-Host '  Created. Back up the "signing" folder: updates must be signed with the same key.' -ForegroundColor Yellow
}
Write-Host '  ok'

Write-Host "`nSetup finished." -ForegroundColor Green
