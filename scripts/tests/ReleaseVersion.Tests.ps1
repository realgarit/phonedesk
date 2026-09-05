BeforeAll {
    $sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $pwsh = (Get-Process -Id $PID).Path
}

Describe 'Release version bump' {
    It 'synchronizes every version source for a <Kind> bump' -ForEach @(
        @{ Kind = 'patch'; Expected = '3.27.1' }
        @{ Kind = 'minor'; Expected = '3.28.0' }
        @{ Kind = 'major'; Expected = '4.0.0' }
    ) {
        $fixture = Join-Path $TestDrive $Kind
        New-Item -ItemType Directory -Path "$fixture/scripts", "$fixture/src/PhoneDesk.Domain" -Force | Out-Null
        Copy-Item -LiteralPath "$sourceRoot/scripts/bump-version.ps1" -Destination "$fixture/scripts/bump-version.ps1"
        '<Project><Version>3.27.0</Version><AssemblyVersion>3.27.0.0</AssemblyVersion><FileVersion>3.27.0.0</FileVersion></Project>' | Set-Content "$fixture/phonedesk.csproj"
        "<assembly>`n  <assemblyIdentity`n    version=`"3.27.0.0`" name=`"PhoneDesk`" />`n</assembly>" | Set-Content "$fixture/app.manifest"
        'public const string Version = "Version 3.27.0";' | Set-Content "$fixture/src/PhoneDesk.Domain/ConstantsService.cs"
        '3.27.0' | Set-Content "$fixture/version.txt"
        & $pwsh -NoProfile -File "$fixture/scripts/bump-version.ps1" -BumpType $Kind | Out-Null
        $LASTEXITCODE | Should -Be 0
        [xml]$project = Get-Content "$fixture/phonedesk.csproj" -Raw
        $project.Project.Version | Should -Be $Expected
        $project.Project.AssemblyVersion | Should -Be "$Expected.0"
        $project.Project.FileVersion | Should -Be "$Expected.0"
        [xml]$manifest = Get-Content "$fixture/app.manifest" -Raw
        $manifest.assembly.assemblyIdentity.version | Should -Be "$Expected.0"
        (Get-Content "$fixture/version.txt" -Raw).Trim() | Should -Be $Expected
        Get-Content "$fixture/src/PhoneDesk.Domain/ConstantsService.cs" -Raw | Should -Match ([regex]::Escape("Version $Expected"))
    }
}
