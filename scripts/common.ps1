$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root  = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root 'tools'
$Dist  = Join-Path $Root 'dist'
$Build = Join-Path $Root 'build'

$env:JAVA_HOME          = Join-Path $Tools 'jdk'
$env:ANDROID_HOME       = Join-Path $Tools 'android-sdk'
$env:ANDROID_SDK_ROOT   = $env:ANDROID_HOME
$env:ANDROID_USER_HOME  = Join-Path $Tools 'android-user'
$env:GRADLE_USER_HOME   = Join-Path $Tools 'gradle-home'
$env:DOTNET_ROOT        = Join-Path $Tools 'dotnet'
$env:DOTNET_CLI_HOME    = Join-Path $Tools 'dotnet-home'
$env:NUGET_PACKAGES     = Join-Path $Tools 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO      = '1'
$env:Path = "$env:DOTNET_ROOT;$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:Path"

$Dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$Adb    = Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe'
$Iscc   = Join-Path $Tools 'innosetup\ISCC.exe'

function Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

function Run([string]$File, [string[]]$Arguments, [string[]]$InputLines) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($InputLines) { $InputLines | & $File @Arguments 2>&1 | ForEach-Object { "$_" } }
        else { & $File @Arguments 2>&1 | ForEach-Object { "$_" } }
        if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $File) exited with code $LASTEXITCODE" }
    } finally { $ErrorActionPreference = $old }
}

function Download([string]$Url, [string]$Out) {
    if (Test-Path $Out) { return }
    Write-Host "  Downloading $Url"
    $tmp = "$Out.part"
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
    Move-Item $tmp $Out
}

function Get-AppVersion {
    $proj = Get-Content (Join-Path $Root 'desktop\SlateCapture\SlateCapture.csproj') -Raw
    if ($proj -match '<Version>([^<]+)</Version>') { return $Matches[1] }
    return '0.0.0'
}
