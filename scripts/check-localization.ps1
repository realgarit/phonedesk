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

$guardProject = Join-Path $RepositoryRoot 'tools/PhoneDesk.LocalizationGuard/PhoneDesk.LocalizationGuard.csproj'
if (-not (Test-Path -LiteralPath $guardProject -PathType Leaf))
{
    throw "Localization guard project not found: $guardProject"
}

& dotnet run --project $guardProject --configuration Release -- $RepositoryRoot
exit $LASTEXITCODE
