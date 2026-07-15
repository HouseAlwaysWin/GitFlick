# The release gate: a Release build plus the full test suite. Called by release.ps1 before it tags,
# and by the CI workflow before it publishes. Keep it fast and deterministic — nothing here should
# touch the working tree.
#
#   powershell -ExecutionPolicy Bypass -File scripts/verify.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Building (Release)..." -ForegroundColor Cyan
dotnet build GitFlick/GitFlick.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test GitFlick.Tests/GitFlick.Tests.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }

Write-Host "Verify passed." -ForegroundColor Green
