# One-time toolchain setup for the web build on Windows without Git Bash (docs/WEB_BUILD.md).
# Same pins as setup.sh: .NET SDK 10.0.402 in %LOCALAPPDATA%\h5web\dotnet10 (no PATH change) + wasm-tools workload
# from workload-rollback.json. Build afterwards with tools/web/build.sh from Git Bash (Git for Windows).
param([switch]$Harness)
$ErrorActionPreference = 'Stop'
$Sdk = '10.0.402'
$Root = if ($env:H5_WEB_DOTNET_ROOT) { $env:H5_WEB_DOTNET_ROOT } else { Join-Path $env:LOCALAPPDATA 'h5web\dotnet10' }
$Dotnet = Join-Path $Root 'dotnet.exe'
New-Item -ItemType Directory -Force $Root | Out-Null
$env:DOTNET_ROOT = $Root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$have = if (Test-Path $Dotnet) { & $Dotnet --list-sdks | Select-String -SimpleMatch "$Sdk " } else { $null }
if (-not $have) {
    $installer = Join-Path $Root 'dotnet-install.ps1'
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
    & $installer -Version $Sdk -InstallDir $Root -NoPath
}
& $Dotnet --list-sdks
& $Dotnet workload install wasm-tools --from-rollback-file (Join-Path $PSScriptRoot 'workload-rollback.json')
& $Dotnet workload list
if ($Harness) {
    Push-Location (Join-Path $PSScriptRoot 'harness')
    try { npm install --no-audit --no-fund; npx playwright-core install chromium } finally { Pop-Location }
}
Write-Host "web toolchain ready: $Dotnet. Build with tools/web/build.sh (Git Bash)"
