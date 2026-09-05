param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$BumpType
)

$ErrorActionPreference = "Stop"

# Get the repo root
$repoRoot = (Split-Path $PSScriptRoot -Parent)
Set-Location $repoRoot

# Read current version from csproj
$csprojPath = Join-Path $repoRoot "phonedesk.csproj"
$csprojContent = Get-Content $csprojPath -Raw

# Extract current version
if ($csprojContent -match '<Version>([\d.]+)</Version>') {
    $currentVersion = $matches[1]
    Write-Host "Current version: $currentVersion" -ForegroundColor Cyan
} else {
    Write-Host "Error: Could not find version in csproj file" -ForegroundColor Red
    exit 1
}

# Parse version (support both 3-part and 4-part versions)
$versionParts = $currentVersion.Split('.')
$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$patch = [int]$versionParts[2]
$revision = if ($versionParts.Length -gt 3) { [int]$versionParts[3] } else { 0 }

# Bump version
switch ($BumpType) {
    'major' {
        $major++
        $minor = 0
        $patch = 0
        $revision = 0
    }
    'minor' {
        $minor++
        $patch = 0
        $revision = 0
    }
    'patch' {
        $patch++
        $revision = 0
    }
}

$displayVersion = "$major.$minor.$patch"
$newVersion = "$displayVersion.$revision"
Write-Host "New version: $newVersion" -ForegroundColor Green

# Update csproj file
$csprojContent = $csprojContent -replace '<Version>[\d.]+</Version>', "<Version>$displayVersion</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d.]+</AssemblyVersion>', "<AssemblyVersion>$newVersion</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d.]+</FileVersion>', "<FileVersion>$newVersion</FileVersion>"
Set-Content -Path $csprojPath -Value $csprojContent -NoNewline
Write-Host "✓ Updated phonedesk.csproj" -ForegroundColor Green

# Update app.manifest
$manifestPath = Join-Path $repoRoot "app.manifest"
$manifestContent = Get-Content $manifestPath -Raw
$manifestContent = $manifestContent -replace '    version="[\d.]+"', "    version=`"$newVersion`""
Set-Content -Path $manifestPath -Value $manifestContent -NoNewline
Write-Host "✓ Updated app.manifest" -ForegroundColor Green

# Update ConstantsService.cs (use 3-part version for display)
$constantsPath = Join-Path $repoRoot "src/PhoneDesk.Domain/ConstantsService.cs"
$constantsContent = Get-Content $constantsPath -Raw
# Use 3-part version for display (major.minor.patch)
$constantsContent = $constantsContent -replace 'public const string Version = "Version [\d.]+";', "public const string Version = `"Version $displayVersion`";"
Set-Content -Path $constantsPath -Value $constantsContent -NoNewline
Write-Host "✓ Updated ConstantsService.cs" -ForegroundColor Green

# Update version.txt (release-please's simple package version source)
$versionFilePath = Join-Path $repoRoot "version.txt"
Set-Content -Path $versionFilePath -Value $displayVersion -NoNewline
Write-Host "✓ Updated version.txt" -ForegroundColor Green

Write-Host ""
Write-Host "Version bumped from $currentVersion to $newVersion" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Review your changes" -ForegroundColor White
Write-Host "  2. Commit and push manually" -ForegroundColor White
