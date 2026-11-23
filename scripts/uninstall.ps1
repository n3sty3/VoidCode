# ==========================================
# VoidCode Per-User Windows Uninstaller
# Removes %LOCALAPPDATA%\Programs\VoidCode, Start Menu/Desktop shortcuts,
# User PATH entry, and Windows Explorer context-menu entries.
# ==========================================
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\VoidCode"
)

$ErrorActionPreference = "Continue"

Write-Host "==> Removing Explorer Context Menu Entries..." -ForegroundColor Cyan
@(
    "HKCU:\Software\Classes\*\shell\VoidCode",
    "HKCU:\Software\Classes\Directory\shell\VoidCode",
    "HKCU:\Software\Classes\Directory\Background\shell\VoidCode",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VoidCode"
) | ForEach-Object {
    if (Test-Path $_) { Remove-Item -Path $_ -Recurse -Force }
}

Write-Host "==> Removing Start Menu & Desktop Shortcuts..." -ForegroundColor Cyan
$shortcuts = @(
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\VoidCode.lnk",
    "$([Environment]::GetFolderPath('Desktop'))\VoidCode.lnk"
)
foreach ($s in $shortcuts) {
    if (Test-Path $s) { Remove-Item $s -Force }
}

Write-Host "==> Removing VoidCode from User PATH..." -ForegroundColor Cyan
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath) {
    $filtered = ($userPath -split ";") | Where-Object { $_ -ne "" -and $_ -ne $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", ($filtered -join ";"), "User")
}

if (Test-Path $InstallDir) {
    Write-Host "==> Removing $InstallDir ..." -ForegroundColor Cyan
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "VoidCode has been uninstalled. User settings under %APPDATA%\VoidCode were preserved." -ForegroundColor Green
