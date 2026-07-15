# Publishes GitFlick (self-contained single-file, same shape as CI) and builds the Inno Setup
# installer locally. Requires Inno Setup 6.3+ (ISCC.exe) — `choco install innosetup` or jrsoftware.org.
#
#   powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1            # version from csproj
#   powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 -Version 1.2.3
#
# Output: installer/output/GitFlick_Setup_<version>.exe
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csprojPath = Join-Path $root "GitFlick\GitFlick.csproj"

if (-not $Version) {
    $csproj = Get-Content $csprojPath -Raw
    if ($csproj -match "<Version>([^<]+)</Version>") { $Version = $Matches[1].Trim() }
    else { throw "Could not read <Version> from $csprojPath" }
}
Write-Host "Building installer for version $Version" -ForegroundColor Cyan

$publishDir = Join-Path $root "publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $csprojPath `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
    "-p:Version=$Version" -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Match the CI artifact: no PDBs.
Get-ChildItem $publishDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it (e.g. 'choco install innosetup') and re-run."
}

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" (Join-Path $root "installer\GitFlick.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Write-Host "Done. Installer:" (Join-Path $root "installer\output\GitFlick_Setup_$Version.exe") -ForegroundColor Green
