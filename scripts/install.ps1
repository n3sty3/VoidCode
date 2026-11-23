# ==========================================
# VoidCode Per-User Windows Installer (No Admin Required)
# Installs to %LOCALAPPDATA%\Programs\VoidCode, creates Start Menu & Desktop
# shortcuts, adds `voidcode` CLI to user PATH, and registers "Open with VoidCode"
# in the Windows Explorer context menu.
# ==========================================
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\VoidCode",
    [switch]$NoDesktopShortcut,
    [switch]$NoContextMenu,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$sourceDir = $PSScriptRoot

if (-not (Test-Path "$sourceDir\VoidCode.exe")) {
    throw "VoidCode.exe was not found in '$sourceDir'. Run this installer from the extracted VoidCode release folder."
}

Write-Host "==> [1/4] Installing VoidCode to $InstallDir ..." -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item -Path "$sourceDir\*" -Destination $InstallDir -Recurse -Force

$exePath = "$InstallDir\VoidCode.exe"
$cmdShimPath = "$InstallDir\voidcode.cmd"
"@echo off`r`nstart `"`" `"%~dp0VoidCode.exe`" %*" | Set-Content -Path $cmdShimPath -Encoding Ascii

Write-Host "==> [2/4] Creating Start Menu & Desktop Shortcuts..." -ForegroundColor Cyan
$wsh = New-Object -ComObject WScript.Shell
$startMenuDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$startShortcut = $wsh.CreateShortcut("$startMenuDir\VoidCode.lnk")
$startShortcut.TargetPath = $exePath
$startShortcut.WorkingDirectory = $InstallDir
$startShortcut.IconLocation = "$exePath,0"
$startShortcut.Description = "VoidCode — AI-Native Windows IDE"
$startShortcut.Save()

if (-not $NoDesktopShortcut) {
    $desktopDir = [Environment]::GetFolderPath("Desktop")
    $deskShortcut = $wsh.CreateShortcut("$desktopDir\VoidCode.lnk")
    $deskShortcut.TargetPath = $exePath
    $deskShortcut.WorkingDirectory = $InstallDir
    $deskShortcut.IconLocation = "$exePath,0"
    $deskShortcut.Description = "VoidCode — AI-Native Windows IDE"
    $deskShortcut.Save()
}

Write-Host "==> [3/4] Adding VoidCode CLI ('voidcode .') to User PATH..." -ForegroundColor Cyan
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathParts = ($userPath -split ";") | Where-Object { $_ -ne "" }
if ($pathParts -notcontains $InstallDir) {
    $newPath = ($pathParts + $InstallDir) -join ";"
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
}

if (-not $NoContextMenu) {
    Write-Host "==> [4/4] Registering 'Open with VoidCode' in Windows Explorer..." -ForegroundColor Cyan
    $regTargets = @(
        "HKCU:\Software\Classes\*\shell\VoidCode",
        "HKCU:\Software\Classes\Directory\shell\VoidCode",
        "HKCU:\Software\Classes\Directory\Background\shell\VoidCode"
    )
    foreach ($key in $regTargets) {
        New-Item -Path "$key\command" -Force | Out-Null
        Set-ItemProperty -Path $key -Name "(Default)" -Value "Open with VoidCode"
        Set-ItemProperty -Path $key -Name "Icon" -Value "`"$exePath`",0"
        $arg = if ($key -like "*Background*") { "`"%V`"" } else { "`"%1`"" }
        Set-ItemProperty -Path "$key\command" -Name "(Default)" -Value "`"$exePath`" $arg"
    }
}

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VoidCode"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "VoidCode IDE"
Set-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "VoidCode Contributors"
Set-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value "$exePath,0"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $InstallDir
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -Type DWord

Write-Host "=================================================" -ForegroundColor Green
Write-Host " VoidCode v1.0.0 Installed Successfully!" -ForegroundColor Green
Write-Host " Location: $InstallDir" -ForegroundColor White
Write-Host " CLI:      voidcode .  (open a new terminal to use)" -ForegroundColor White
Write-Host "=================================================" -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process -FilePath $exePath
}
