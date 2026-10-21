<#
.SYNOPSIS
    Installs Desktop Janitor for the current user. No admin rights needed.

.DESCRIPTION
    1. Builds a fresh self-contained exe into publish\ if a .NET 10 SDK is found
       (skip with -SkipBuild to reuse the exe that's already there).
    2. Closes a running janitor politely (--exit) so it can be replaced.
    3. Copies it to %LOCALAPPDATA%\Programs\DesktopJanitor.
    4. Adds a Start menu shortcut and an entry under Settings > Apps > Installed apps,
       so it can be uninstalled from there (or with uninstall.ps1).
    5. Starts it.
    Your settings (%APPDATA%\DesktopJanitor) are left alone.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [switch]$SkipBuild,
    [switch]$DesktopShortcut,
    [switch]$StartWithWindows,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$source = Join-Path $root 'publish\DesktopJanitor.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DesktopJanitor'
$target = Join-Path $installDir 'DesktopJanitor.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Desktop Janitor.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desktop Janitor.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopJanitor'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Where a .NET 10 SDK may live: per-user installs first (%USERPROFILE%\.dotnet, then the
# dotnet-install.ps1 default), then whatever `dotnet` is on PATH, then the machine-wide one.
$dotnetCandidates = @(
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
) | Where-Object { $_ } | Select-Object -Unique

function Find-Dotnet10 {
    foreach ($dotnet in ($dotnetCandidates | Where-Object { Test-Path $_ })) {
        if ((& $dotnet --list-sdks 2>$null) -match '^10\.') { return $dotnet }
    }
    return $null
}

function Stop-Janitor {
    if (-not (Get-Process DesktopJanitor -ErrorAction SilentlyContinue)) { return }
    Write-Host 'Asking the running janitor to clock off...'
    # Send --exit through the publish\ copy if there is one, so the installed exe isn't launched
    # (and rescanned by antivirus) right before we overwrite it.
    $exe = @($source, $target) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($exe) { & $exe --exit }
    for ($i = 0; $i -lt 20 -and (Get-Process DesktopJanitor -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    # Older builds don't understand --exit. The janitor holds no data, so stopping it is safe.
    Get-Process DesktopJanitor -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Copy-WithRetry([string]$from, [string]$to) {
    # Right after the janitor quits, antivirus (or Windows) can keep the old exe open for a few seconds.
    for ($attempt = 1; ; $attempt++) {
        try {
            Copy-Item $from $to -Force
            return
        }
        catch {
            if (-not ($_.Exception -is [System.IO.IOException]) -or $attempt -ge 30) {
                throw "Couldn't replace $to; something still has it open (often an antivirus scan). Wait a minute and run install.ps1 again. ($($_.Exception.Message))"
            }
            if ($attempt -eq 1) { Write-Host 'The old exe is still in use (antivirus scanning it?), waiting...' }
            Start-Sleep -Seconds 1
        }
    }
}

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$target,0"
    $link.Description = 'A little janitor who sweeps above your taskbar and tidies idle apps'
    $link.Save()
}

# 1. Build
if (-not $SkipBuild) {
    $dotnet = Find-Dotnet10
    if ($dotnet) {
        Write-Host "Building with $dotnet ..."
        Stop-Janitor # a running copy from publish\ would lock the exe
        & $dotnet publish (Join-Path $root 'src\DesktopJanitor') -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $root 'publish') -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was installed.' }
    }
    elseif (Test-Path $source) {
        Write-Host 'No .NET 10 SDK found, so installing the exe already in publish\ without rebuilding. Looked in:'
        $dotnetCandidates | ForEach-Object { Write-Host "  $_" }
    }
}
if (-not (Test-Path $source)) {
    throw "No exe at $source. Install the .NET 10 SDK, or publish first (see README)."
}

# 2-3. Replace any running copy and copy the files in
Stop-Janitor
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-WithRetry $source $target
Copy-Item (Join-Path $root 'uninstall.ps1') $installDir -Force

# 4. Shortcuts and the Installed apps entry
New-Shortcut $startMenuLink
if ($DesktopShortcut) { New-Shortcut $desktopLink }

$version = ((Get-Item $target).VersionInfo.ProductVersion -split '\+')[0]
New-Item -Path $uninstallKey -Force | Out-Null
$entry = @{
    DisplayName     = 'Desktop Janitor'
    DisplayVersion  = $version
    Publisher       = 'Desktop Janitor'
    DisplayIcon     = $target
    InstallLocation = $installDir
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\uninstall.ps1`""
}
foreach ($name in $entry.Keys) { Set-ItemProperty -Path $uninstallKey -Name $name -Value $entry[$name] }
Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value ([int]((Get-Item $target).Length / 1KB)) -Type DWord

# Start with Windows: on request, or keep an existing entry pointing at the installed copy.
$existingRun = (Get-ItemProperty -Path $runKey -Name DesktopJanitor -ErrorAction SilentlyContinue).DesktopJanitor
if ($StartWithWindows -or $existingRun) {
    Set-ItemProperty -Path $runKey -Name DesktopJanitor -Value "`"$target`""
}

Write-Host ''
Write-Host "Installed Desktop Janitor $version to $installDir"
Write-Host "  Start menu: Desktop Janitor$(if ($DesktopShortcut) { ' (plus a desktop shortcut)' })"
Write-Host "  Start with Windows: $(if ($StartWithWindows -or $existingRun) { 'on' } else { 'off (turn it on from his tray menu)' })"
Write-Host '  Uninstall: Settings > Apps > Installed apps > Desktop Janitor'

# 5. Start
if (-not $NoLaunch) {
    # Via Explorer, like a Start menu launch: inherits nothing from this shell and never runs elevated.
    & explorer.exe $target
    Write-Host 'He''s on shift: look just above your taskbar, and for his face in the tray.'
}
