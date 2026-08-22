[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot))
{
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
else
{
    $RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
}

$hookPath = Join-Path $RepositoryRoot '.githooks'
$hookFile = Join-Path $hookPath 'pre-commit'
if (-not (Test-Path -LiteralPath $hookFile -PathType Leaf))
{
    throw "Pre-commit hook not found: $hookFile"
}

$existingHookPath = (& git -C $RepositoryRoot config --get core.hooksPath 2>$null | Select-Object -First 1)
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$existingHookPath))
{
    $existingHookPath = ([string]$existingHookPath).Trim()
    if ($existingHookPath -ne '.githooks')
    {
        throw "An existing core.hooksPath ('$existingHookPath') is configured. Refusing to replace it; preserve that configuration and install the localization hook manually."
    }
}

& git -C $RepositoryRoot config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0)
{
    throw 'Git could not set core.hooksPath.'
}

$chmod = Get-Command chmod -ErrorAction SilentlyContinue
if ($null -ne $chmod)
{
    & $chmod.Source +x $hookFile
}

Write-Host 'Localization pre-commit hook installed for this repository.'
Write-Host 'The hook runs only when staged UI, localization, guard, or build files change.'
Write-Host 'To remove it, restore the previous core.hooksPath value. If no value existed before installation: git config --unset core.hooksPath'
