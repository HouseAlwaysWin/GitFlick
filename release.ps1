# Cuts a release: verifies, bumps the version, promotes the changelog, then commits + tags + pushes
# so the GitHub Actions workflow (.github/workflows/release.yml) builds and publishes it.
#
#   powershell -ExecutionPolicy Bypass -File release.ps1 v1.2.3
#   powershell -ExecutionPolicy Bypass -File release.ps1          # prompts for the version
#
# Preconditions: on master, clean working tree, the tag must not already exist, and CHANGELOG.md must
# have an "## Unreleased" (or an "## v<version>") section. Rolls the generated edits back on failure.
[CmdletBinding()]
param([string]$version)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not $version) {
    $version = Read-Host "Enter version to release (e.g., v1.0.0)"
}

if ($version -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version must use semantic version format and start with 'v' (e.g., v1.0.0)."
}

$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to determine the current Git branch." }
if ($branch -ne "master") { throw "Releases must be created from master. Current branch: $branch" }

if (@(git status --porcelain).Count -gt 0) {
    throw "The working tree must be clean before starting a release."
}

if (git tag -l $version) {
    throw "Tag $version already exists locally. Use a new version instead of moving a release tag."
}

& git ls-remote --exit-code --tags origin "refs/tags/$version" | Out-Null
$remoteTagExitCode = $LASTEXITCODE
if ($remoteTagExitCode -eq 0) {
    throw "Tag $version already exists on origin. Use a new version instead of moving a release tag."
}
if ($remoteTagExitCode -ne 2) {
    throw "Unable to check whether tag $version exists on origin."
}

# The release commit bumps the csproj AND promotes the changelog; any OTHER dirty file aborts.
$versionPattern = [regex]::Escape($version.TrimStart('v'))
$changelog = Get-Content -LiteralPath "CHANGELOG.md" -Raw -Encoding UTF8
$hasVersionSection = $changelog -match "(^|\n)##[^\n]*\bv?$versionPattern\b"
$hasUnreleasedSection = $changelog -match "(?im)^##[ \t]+Unreleased\b"
if (-not $hasVersionSection -and -not $hasUnreleasedSection) {
    throw ("CHANGELOG.md has no section for $version and no '## Unreleased' section to promote. Add an " +
        "'## Unreleased' entry before releasing so the release log stays current.")
}

Write-Host "Starting release process for $version..." -ForegroundColor Cyan

$csprojPath = "GitFlick/GitFlick.csproj"
$rollbackFiles = @($csprojPath, "CHANGELOG.md")
$versionPlain = $version.TrimStart('v')
# MSBuild imports process env vars as properties; a stray VERSION would override the project's Version.
$inheritedVersion = [Environment]::GetEnvironmentVariable("VERSION", "Process")
$releaseCommitted = $false

try {
    [Environment]::SetEnvironmentVariable("VERSION", $null, "Process")

    Write-Host "Verifying current master before changing the version..." -ForegroundColor Gray
    & (Join-Path $root "scripts/verify.ps1")

    if (@(git status --porcelain).Count -gt 0) {
        throw "Verification modified tracked files; aborting."
    }

    # Promote "## Unreleased" -> "## <version> - <today>" (only if a version section wasn't hand-written).
    if (-not $hasVersionSection) {
        $releaseDate = Get-Date -Format 'yyyy-MM-dd'
        Write-Host "Promoting CHANGELOG '## Unreleased' to '## $version - $releaseDate'..." -ForegroundColor Gray
        $unreleasedRegex = [regex]::new('(?im)^##[ \t]+Unreleased\b.*$')
        $promoted = $unreleasedRegex.Replace($changelog, "## $version - $releaseDate", 1)
        if ($promoted -eq $changelog) { throw "Failed to promote the '## Unreleased' section in CHANGELOG.md." }
        [IO.File]::WriteAllText((Resolve-Path "CHANGELOG.md"), $promoted, [Text.UTF8Encoding]::new($false))
    }

    Write-Host "Updating $csprojPath to version $versionPlain..." -ForegroundColor Gray
    $csproj = Get-Content -LiteralPath $csprojPath -Raw
    $updatedCsproj = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$versionPlain</Version>", 1)
    if ($updatedCsproj -eq $csproj) { throw "Version $versionPlain is already set or <Version> was not found." }
    [IO.File]::WriteAllText((Resolve-Path $csprojPath), $updatedCsproj, [Text.UTF8Encoding]::new($false))

    $changedFiles = @(git status --porcelain | ForEach-Object { $_.Substring(3) })
    $allowed = @($csprojPath, "CHANGELOG.md")
    $unexpected = @($changedFiles | Where-Object { $allowed -notcontains $_ })
    if ($unexpected.Count -gt 0) { throw "Release produced unexpected changes: $($unexpected -join ', ')" }

    Invoke-Checked -Command "git" -Arguments @("add", "--", $csprojPath, "CHANGELOG.md")
    Invoke-Checked -Command "git" -Arguments @("commit", "-m", "chore: release $version")
    $releaseCommitted = $true

    Invoke-Checked -Command "git" -Arguments @("tag", "-a", $version, "-m", "Release $version")

    Write-Host "Pushing release commit and tag to GitHub..." -ForegroundColor Cyan
    Invoke-Checked -Command "git" -Arguments @("push", "--atomic", "origin", "master", $version)

    Write-Host "Successfully triggered release! Check GitHub Actions." -ForegroundColor Green
}
catch {
    if (-not $releaseCommitted) {
        Write-Warning "Release failed before commit. Reverting generated release changes."
        & dotnet build-server shutdown | Out-Null
        & git restore --source=HEAD --staged --worktree -- $rollbackFiles
    }
    throw
}
finally {
    [Environment]::SetEnvironmentVariable("VERSION", $inheritedVersion, "Process")
}
