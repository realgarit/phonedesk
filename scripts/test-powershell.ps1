$ErrorActionPreference = 'Stop'
if (-not (Get-Module -ListAvailable Pester | Where-Object Version -EQ '5.7.1')) {
    Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser -Force -SkipPublisherCheck
}
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester -Path (Join-Path $PSScriptRoot 'tests') -CI
