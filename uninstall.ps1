<#
.SYNOPSIS
    Removes Desktop Janitor installed by install.ps1 (also what Settings > Apps > Uninstall runs).

.DESCRIPTION
    Closes the janitor, then removes the installed exe, the Start menu and desktop shortcuts,
    the Installed apps entry and the start-up entry. Your settings and log are kept unless you
    pass -RemoveSettings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveSettings
#>
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DesktopJanitor'
$target = Join-Path $installDir 'DesktopJanitor.exe'
$links = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Desktop Janitor.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desktop Janitor.lnk')
)
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopJanitor'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if (Get-Process DesktopJanitor -ErrorAction SilentlyContinue) {
    Write-Host 'Asking the janitor to clock off...'
    if (Test-Path $target) { & $target --exit }
    for ($i = 0; $i -lt 20 -and (Get-Process DesktopJanitor -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Get-Process DesktopJanitor -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

foreach ($link in $links) { if (Test-Path $link) { Remove-Item $link -Force } }

# Only remove the start-up entry if it points at the installed copy.
$run = (Get-ItemProperty -Path $runKey -Name DesktopJanitor -ErrorAction SilentlyContinue).DesktopJanitor
if ($run -and $run -like "*$installDir*") { Remove-ItemProperty -Path $runKey -Name DesktopJanitor }

if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }

# WPF's native DLLs unpacked by the single-file exe on first start.
$extracted = Join-Path $env:TEMP '.net\DesktopJanitor'
if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue }

# This script may be running from inside $installDir, so step out first.
Set-Location $env:TEMP
if (Test-Path $installDir) {
    try {
        Remove-Item $installDir -Recurse -Force
    }
    catch {
        # Still in use for a moment; delete it once this window closes.
        Start-Process cmd.exe -ArgumentList "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q `"$installDir`"" -WindowStyle Hidden
    }
}

if ($RemoveSettings) {
    foreach ($dir in @((Join-Path $env:APPDATA 'DesktopJanitor'), (Join-Path $env:LOCALAPPDATA 'DesktopJanitor'))) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    Write-Host 'Desktop Janitor and its settings have been removed.'
}
else {
    Write-Host 'Desktop Janitor has been removed.'
    Write-Host "Your settings and log are kept in $env:APPDATA\DesktopJanitor and $env:LOCALAPPDATA\DesktopJanitor (run with -RemoveSettings to delete them)."
}
