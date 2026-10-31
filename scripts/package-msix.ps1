<#
.SYNOPSIS
    Builds the Microsoft Store MSIX package from the self-contained win-x64 publish output.

.DESCRIPTION
    Copies the publish output and the MSIX visual assets into a staging layout, fills the
    AppxManifest.xml template (installer/msix) and runs makeappx.exe from the Windows SDK.

    The Microsoft Store re-signs MSIX packages during publishing, so the release package is
    produced unsigned. -TestCertificate signs the package with a throwaway self-signed
    certificate for local sideload smoke tests only; the certificate is never kept.

.PARAMETER PublishDir
    Output of `dotnet publish phonedesk.csproj -r win-x64 --self-contained`.

.PARAMETER Version
    Three-part application version (X.Y.Z). The package version becomes X.Y.Z.0; the Store
    reserves the fourth part.

.PARAMETER OutputDir
    Directory that receives phonedesk-win-x64.msix.

.PARAMETER IdentityName
    Package/Identity/Name from Partner Center. Defaults to a development identity.

.PARAMETER Publisher
    Package/Identity/Publisher from Partner Center (CN=<GUID>). Defaults to a development identity.

.PARAMETER PublisherDisplayName
    Human-readable publisher name shown by Windows.

.EXAMPLE
    ./scripts/package-msix.ps1 -PublishDir ./publish/win-x64 -Version 3.34.0 -OutputDir ./dist `
        -IdentityName 12345PatrikLleshaj.PhoneDesk -Publisher 'CN=00000000-0000-0000-0000-000000000000'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDir,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$OutputDir,

    [string]$IdentityName = 'PhoneDesk.Dev',
    [string]$Publisher = 'CN=PhoneDesk Development',
    [string]$PublisherDisplayName = 'Patrik Lleshaj',
    [string]$DisplayName = 'PhoneDesk',
    [string]$PackageFileName = 'phonedesk-win-x64.msix',
    [switch]$TestCertificate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# CI passes repository variables straight through; empty means "not configured yet".
if ([string]::IsNullOrWhiteSpace($IdentityName)) { $IdentityName = 'PhoneDesk.Dev' }
if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = 'CN=PhoneDesk Development' }
if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) { $PublisherDisplayName = 'Patrik Lleshaj' }
if ([string]::IsNullOrWhiteSpace($DisplayName)) { $DisplayName = 'PhoneDesk' }

if (-not $IsWindows) {
    throw 'MSIX packaging requires Windows (makeappx.exe from the Windows SDK).'
}

function Find-SdkTool {
    param([Parameter(Mandatory)][string]$Name)

    $roots = @()
    $kitsRoot = Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10 -ErrorAction SilentlyContinue
    if ($kitsRoot) { $roots += $kitsRoot }
    $roots += "${env:ProgramFiles(x86)}\Windows Kits\10"
    $roots += "$env:ProgramFiles\Windows Kits\10"

    foreach ($root in ($roots | Where-Object { $_ } | Select-Object -Unique)) {
        $binRoot = Join-Path $root 'bin'
        if (-not (Test-Path $binRoot)) { continue }

        $candidate = Get-ChildItem -Path $binRoot -Directory -Filter '10.*' |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }

    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw "$Name was not found. Install the Windows 10/11 SDK (Windows App Certification Kit component)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$templatePath = Join-Path $repoRoot 'installer\msix\AppxManifest.xml'
$assetsPath = Join-Path $repoRoot 'installer\msix\Assets'

$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'phonedesk.exe'))) {
    throw "phonedesk.exe was not found in '$PublishDir'. Publish win-x64 first."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$packagePath = Join-Path $OutputDir $PackageFileName

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("phonedesk-msix-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Write-Host "Staging publish output from $PublishDir"
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $staging -Recurse -Force
    Copy-Item -Path $assetsPath -Destination (Join-Path $staging 'Assets') -Recurse -Force

    # Debug symbols are not shipped in the Store package.
    Get-ChildItem -Path $staging -Filter '*.pdb' -Recurse | Remove-Item -Force

    # Save-Module leaves NuGet packaging leftovers inside each module folder. PowerShell never
    # reads them, and "[Content_Types].xml" is a reserved OPC name that makeappx rejects
    # (0x8007007b), so drop them from the staged copy only.
    $modulesRoot = Join-Path $staging 'Modules'
    if (Test-Path $modulesRoot) {
        foreach ($module in Get-ChildItem -Path $modulesRoot -Directory) {
            foreach ($leftover in @('[Content_Types].xml', '_rels', 'package')) {
                $path = Join-Path $module.FullName $leftover
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            }
            Get-ChildItem -LiteralPath $module.FullName -Filter '*.nuspec' -File | Remove-Item -Force
        }
    }

    $manifest = Get-Content -Path $templatePath -Raw
    $manifest = $manifest.Replace('__IDENTITY_NAME__', $IdentityName)
    $manifest = $manifest.Replace('__PUBLISHER__', $Publisher)
    $manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName)
    $manifest = $manifest.Replace('__DISPLAY_NAME__', $DisplayName)
    $manifest = $manifest.Replace('__VERSION__', "$Version.0")
    if ($manifest -match '__[A-Z_]+__') {
        throw "AppxManifest.xml still contains an unreplaced placeholder: $($Matches[0])"
    }

    $manifestPath = Join-Path $staging 'AppxManifest.xml'
    [System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

    # Index the qualified assets (scale-*, targetsize-*, altform-unplated). Without
    # resources.pri Windows resolves only the literal file names from the manifest, never
    # finds the unplated taskbar icons, and plates the logo with the accent colour.
    $makepri = Find-SdkTool -Name 'makepri.exe'
    $priConfig = Join-Path $staging 'priconfig.xml'
    & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o
    if ($LASTEXITCODE -ne 0) {
        throw "makepri.exe createconfig failed with exit code $LASTEXITCODE."
    }
    & $makepri new /pr $staging /cf $priConfig /mn $manifestPath /of (Join-Path $staging 'resources.pri') /o
    if ($LASTEXITCODE -ne 0) {
        throw "makepri.exe new failed with exit code $LASTEXITCODE."
    }
    Remove-Item -LiteralPath $priConfig -Force
    Get-ChildItem -Path $staging -Filter 'resources.*.pri' -File | Remove-Item -Force

    $makeappx = Find-SdkTool -Name 'makeappx.exe'
    Write-Host "Packing with $makeappx"
    if (Test-Path $packagePath) { Remove-Item $packagePath -Force }
    & $makeappx pack /o /h SHA256 /d $staging /p $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe failed with exit code $LASTEXITCODE."
    }

    if ($TestCertificate) {
        $signtool = Find-SdkTool -Name 'signtool.exe'
        # Reuse the test certificate across rebuilds so the machine-level trust (which
        # needs elevation) is set up once. Remove it from Cert:\CurrentUser\My and from
        # Cert:\LocalMachine\TrustedPeople when the sideload tests are over.
        $friendlyName = 'PhoneDesk MSIX test signing'
        $cert = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
            Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq $friendlyName -and $_.NotAfter -gt (Get-Date).AddDays(1) } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if (-not $cert) {
            $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
                -KeyUsage DigitalSignature -FriendlyName $friendlyName `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
            Write-Host "Created test certificate $($cert.Thumbprint) in Cert:\CurrentUser\My"
        }
        $cerPath = [System.IO.Path]::ChangeExtension($packagePath, '.test.cer')
        Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "signtool.exe failed with exit code $LASTEXITCODE."
        }
        Write-Host "Signed with test certificate $($cert.Thumbprint); public key exported to $cerPath"
    }

    $size = (Get-Item $packagePath).Length
    Write-Host ("Created {0} ({1:N1} MB) with identity {2} / {3} version {4}.0" -f $packagePath, ($size / 1MB), $IdentityName, $Publisher, $Version)
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
