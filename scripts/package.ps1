# ==========================================
# VoidCode Production Release Packaging Script ()
# ==========================================
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [switch]$SkipUiChecks
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\.."
$node = (Get-Command node -ErrorAction Stop).Source
$npm = (Get-Command npm.cmd -ErrorAction SilentlyContinue)
$outDir = "$root\dist"
$stageDir = "$outDir\VoidCode-$Runtime"
$zipPath = "$outDir\VoidCode-$Runtime.zip"
$shaPath = "$zipPath.sha256"
$selfContainedFlag = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }

Write-Host "==> [1/5] Running Metrics Ratchet & JS Quality Checks..." -ForegroundColor Cyan
& $node "$root\scripts\metrics.js" --check
if ($LASTEXITCODE -ne 0) { throw "Metrics ratchet check failed." }

if (-not $SkipUiChecks -and $npm) {
    Push-Location $root
    try {
        & $npm.Source run lint -- --max-warnings=0
        if ($LASTEXITCODE -ne 0) { throw "ESLint check failed." }
        & $npm.Source run typecheck
        if ($LASTEXITCODE -ne 0) { throw "TypeScript checkJs failed." }
        & $npm.Source run test:unit
        if ($LASTEXITCODE -ne 0) { throw "Node unit tests failed." }
        & $npm.Source run audit:payloads
        if ($LASTEXITCODE -ne 0) { throw "Bridge payload audit failed." }
    }
    finally {
        Pop-Location
    }
}

Write-Host "==> [2/5] Running .NET Unit & Integration Tests..." -ForegroundColor Cyan
$testProject = "$root\tests\VoidCode.Tests\VoidCode.Tests.csproj"
if (-not (Test-Path $testProject)) { throw "VoidCode.Tests.csproj was not found." }
$testConfig = if (Get-Process -Name "VoidCode" -ErrorAction SilentlyContinue) { "Debug" } else { "Release" }
dotnet test $testProject -c $testConfig --nologo
if ($LASTEXITCODE -ne 0) { throw ".NET test suite failed." }

Write-Host "==> [3/5] Publishing Release Binaries ($Runtime, $selfContainedFlag)..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

if (Get-Process -Name "VoidCode" -ErrorAction SilentlyContinue) {
    $altOut = "$root/obj/pub_stage/$Runtime"
    dotnet publish "$root\VoidCode.csproj" -c Release -r $Runtime $selfContainedFlag "-p:OutputPath=$altOut" -o $stageDir --nologo
} else {
    dotnet publish "$root\VoidCode.csproj" -c Release -r $Runtime $selfContainedFlag -o $stageDir --nologo
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item "$root\wwwroot" "$stageDir\wwwroot" -Recurse -Force
Copy-Item "$root\scripts\install.ps1" "$stageDir\install.ps1" -Force
Copy-Item "$root\scripts\uninstall.ps1" "$stageDir\uninstall.ps1" -Force
"@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`" %*" | Set-Content -Path "$stageDir\Install-VoidCode.cmd" -Encoding Ascii
"@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall.ps1`" %*" | Set-Content -Path "$stageDir\Uninstall-VoidCode.cmd" -Encoding Ascii
foreach ($doc in @("README.md", "CHANGELOG.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "SECURITY.md", "PRIVACY.md")) {
    if (Test-Path "$root\$doc") {
        Copy-Item "$root\$doc" "$stageDir\$doc" -Force
    }
}

Write-Host "==> [4/5] Creating Release Archive & SHA-256 Checksum..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -Force

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -Path $shaPath -Encoding Ascii
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)

$csprojXml = [xml](Get-Content "$root\VoidCode.csproj")
$appVersion = ($csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $appVersion) { $appVersion = "1.1.1" }
$setupExePath = "$outDir\VoidCode-Setup-$appVersion-$Runtime.exe"
$setupShaPath = "$setupExePath.sha256"
$cscPath = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (Test-Path $cscPath) {
    Write-Host "==> [5/6] Building Standalone Windows GUI Installer ($(Split-Path -Leaf $setupExePath))..." -ForegroundColor Cyan
    if (Test-Path $setupExePath) { Remove-Item $setupExePath -Force }
    $fwDir = Split-Path -Parent $cscPath
    $wpfDir = "$fwDir\WPF"
    & $cscPath /nologo /target:winexe /optimize+ "/out:$setupExePath" "/win32icon:$root\app.ico" /r:System.dll /r:System.Windows.Forms.dll /r:System.Xaml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/r:$wpfDir\PresentationFramework.dll" "/r:$wpfDir\PresentationCore.dll" "/r:$wpfDir\WindowsBase.dll" "$root\scripts\VoidCodeSetup.cs"
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile VoidCodeSetup.cs" }

    $outStream = [System.IO.File]::Open($setupExePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $inStream = [System.IO.File]::OpenRead($zipPath)
        try {
            $zipLen = [int64]$inStream.Length
            $inStream.CopyTo($outStream)
        }
        finally {
            $inStream.Dispose()
        }
        $lenBytes = [System.BitConverter]::GetBytes($zipLen)
        $magicBytes = [System.Text.Encoding]::ASCII.GetBytes("VCSFX100")
        $outStream.Write($lenBytes, 0, 8)
        $outStream.Write($magicBytes, 0, 8)
    }
    finally {
        $outStream.Dispose()
    }
    $setupHash = (Get-FileHash -Path $setupExePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  $(Split-Path -Leaf $setupExePath)" | Set-Content -Path $setupShaPath -Encoding Ascii
    $setupSizeMb = [math]::Round((Get-Item $setupExePath).Length / 1MB, 2)
}

Write-Host "==> [6/6] Complete!" -ForegroundColor Green
Write-Host "=================================================" -ForegroundColor Green
Write-Host " VoidCode Release Packages Created ($Runtime)" -ForegroundColor Green
if (Test-Path $setupExePath) {
    Write-Host " Installer: $setupExePath ($setupSizeMb MB)" -ForegroundColor White
    Write-Host " SHA256:    $setupHash" -ForegroundColor White
}
Write-Host " Portable:  $zipPath ($sizeMb MB)" -ForegroundColor White
Write-Host " SHA256:    $hash" -ForegroundColor White
Write-Host "=================================================" -ForegroundColor Green
