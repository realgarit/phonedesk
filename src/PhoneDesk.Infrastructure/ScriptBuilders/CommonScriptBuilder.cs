using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;

namespace PhoneDesk.Services.ScriptBuilders
{
    public class CommonScriptBuilder
    {
        private readonly IPowerShellSanitizationService _sanitizer;

        public CommonScriptBuilder(IPowerShellSanitizationService sanitizer)
        {
            _sanitizer = sanitizer;
        }

        public string GetCheckModulesCommand()
        {
            // Reuse GetCommonSetupScript() to avoid duplicating TLS/WAM/PSModulePath bootstrapping
            return GetCommonSetupScript() + @"
$output = @()
$output += 'Common setup completed successfully'

# Check MicrosoftTeams module
if (Get-Module -ListAvailable -Name " + ConstantsService.PowerShellModules.MicrosoftTeams + @") {
    $teamsModule = Get-Module -ListAvailable -Name " + ConstantsService.PowerShellModules.MicrosoftTeams + @"
    $output += 'MicrosoftTeams module is available: ' + $teamsModule.Version
} else {
    $output += 'ERROR: MicrosoftTeams module not found in bundled modules'
}

# Check Microsoft.Graph modules
$requiredGraphModules = @(
    """ + ConstantsService.PowerShellModules.MicrosoftGraphAuthentication + @""",
    """ + ConstantsService.PowerShellModules.MicrosoftGraphUsers + @""",
    """ + ConstantsService.PowerShellModules.MicrosoftGraphUsersActions + @""",
    """ + ConstantsService.PowerShellModules.MicrosoftGraphGroups + @""",
    """ + ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement + @"""
)

foreach ($moduleName in $requiredGraphModules) {
    if (Get-Module -ListAvailable -Name $moduleName) {
        $module = Get-Module -ListAvailable -Name $moduleName
        $output += ""$moduleName module is available: $($module.Version)""
    } else {
        $output += ""ERROR: $moduleName module not found in bundled modules""
    }
}

$output | ForEach-Object { Write-Host $_ }
";
        }

        public string GetCommonSetupScript()
        {
            return @"
# Common Setup
$ErrorActionPreference = 'Stop'

# Force MSAL and Azure.Identity to use system browser instead of WAM (Legacy/Compat)
$env:MSAL_DISABLE_WAM = 'true'
$env:AZURE_IDENTITY_DISABLE_WAM = 'true'

# Enable TLS 1.2
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Add bundled modules to PSModulePath
$appDir = [System.IO.Path]::GetDirectoryName([System.Reflection.Assembly]::GetExecutingAssembly().Location)
if (-not $appDir) {
    $appDir = [System.AppDomain]::CurrentDomain.BaseDirectory
}
$possiblePaths = @(
    (Join-Path $appDir 'Modules'),
    (Join-Path $appDir 'win-x64/Modules'),
    (Join-Path $appDir 'osx-x64/Modules'),
    (Join-Path $appDir 'osx-arm64/Modules'),
    (Join-Path $appDir 'linux-x64/Modules'),
    (Join-Path $appDir '../Modules'),
    (Join-Path $appDir '../win-x64/Modules'),
    (Join-Path $appDir '../osx-x64/Modules'),
    (Join-Path $appDir '../osx-arm64/Modules'),
    (Join-Path $appDir '../linux-x64/Modules')
)

$bundledModulesPath = $null
foreach ($path in $possiblePaths) {
    if (Test-Path $path) {
        $bundledModulesPath = $path
        break
    }
}

if ($bundledModulesPath) {
    $pathSeparator = [System.IO.Path]::PathSeparator
    $currentPaths = $env:PSModulePath -split $pathSeparator
    if ($bundledModulesPath -notin $currentPaths) {
        $env:PSModulePath = $bundledModulesPath + $pathSeparator + $env:PSModulePath
    }
}
";
        }

        public string GetConnectTeamsCommand()
        {
            return GetCommonSetupScript() + @"
try {
    # Explicitly import MicrosoftTeams to ensure cmdlets are available
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftTeams + @" -Force -ErrorAction Stop

    # MicrosoftTeams 7.9.0 enables WAM by default on Windows. PhoneDesk hosts
    # PowerShell in-process and has no console HWND for the module to parent WAM to.
    # Use Microsoft's documented compatibility switch for unsupported hosts.
    Connect-MicrosoftTeams -DisableWAM -ErrorAction Stop
    $connection = Get-CsTenant -ErrorAction Stop
    if ($connection) {
        Write-Host 'SUCCESS: Connected to Microsoft Teams'
        Write-Host ""Connected to tenant: $($connection.DisplayName) ($($connection.TenantId))""
    }
}
catch {
    Write-Host ""ERROR: Failed to connect to Microsoft Teams: $_""
}";
        }

        public string GetConnectGraphWithTokenCommand(string accessToken)
        {
            // SECURITY: Pass the access token via environment variable to avoid logging it in clear text.
            // The environment variable is cleared immediately after use.
            // Using a unique variable name to avoid conflicts.
            return GetCommonSetupScript() + @"
try {
    # Import Microsoft.Graph modules
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphAuthentication + @" -Force
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphUsers + @" -Force
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphUsersActions + @" -Force
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphGroups + @" -Force
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement + @" -Force

    # Convert the access token to SecureString as required by Connect-MgGraph
    # Token is passed via environment variable to avoid logging
    $tokenSecure = ConvertTo-SecureString -String $env:TEAMS_PM_TOKEN -AsPlainText -Force
    
    # Clear the environment variable immediately for security
    $env:TEAMS_PM_TOKEN = $null
    [GC]::Collect()

    Connect-MgGraph -AccessToken $tokenSecure -ErrorAction Stop -NoWelcome
    $context = Get-MgContext -ErrorAction Stop
    if ($context) {
        Write-Host 'SUCCESS: Connected to Microsoft Graph'
        Write-Host ""Connected as: $($context.Account)""
    }
}
catch {
    # Ensure token is cleared even on error
    $env:TEAMS_PM_TOKEN = $null
    Write-Host ""ERROR: Failed to connect to Microsoft Graph: $_""
}";
        }

        public string GetDisconnectTeamsCommand()
        {
            return @"
try {
    Disconnect-MicrosoftTeams -ErrorAction Stop
    Write-Host 'SUCCESS: Disconnected from Microsoft Teams'
}
catch {
    Write-Host ""ERROR: Failed to disconnect from Microsoft Teams: $_""
}";
        }

        public string GetDisconnectGraphCommand()
        {
            return @"
try {
    # Import Microsoft.Graph modules
    Import-Module " + ConstantsService.PowerShellModules.MicrosoftGraphAuthentication + @" -Force

    Disconnect-MgGraph -ErrorAction Stop
    Write-Host 'SUCCESS: Disconnected from Microsoft Graph'
}
catch {
    Write-Host ""ERROR: Failed to disconnect from Microsoft Graph: $_""
}";
        }

        public string GetImportAudioFileCommand(string filePath)
        {
            // Escape single quotes for PowerShell (use single quotes to avoid issues with spaces and special chars)
            var escapedPath = filePath.Replace("'", "''");
            // Extract just the filename from the path
            var fileName = System.IO.Path.GetFileName(filePath).Replace("'", "''");

            return $@"
try {{
    # Read the audio file content as bytes (compatible with both PowerShell 5.x and 6+)
    $fileContent = $null
    if ($PSVersionTable.PSVersion.Major -ge 6) {{
        $fileContent = Get-Content -Path '{escapedPath}' -AsByteStream -ReadCount 0
    }} else {{
        $fileContent = Get-Content -Path '{escapedPath}' -Encoding Byte -ReadCount 0
    }}

    if (-not $fileContent) {{
        Write-Host ""ERROR: Failed to read audio file content""
        exit
    }}

    # Import the audio file (using HuntGroup ApplicationId for Call Queues)
    $audioFile = Import-CsOnlineAudioFile -ApplicationId HuntGroup -FileName '{fileName}' -Content $fileContent
    if ($audioFile) {{
        Write-Host ""SUCCESS: Audio file imported successfully""
        Write-Host ""AUDIOFILEID: $($audioFile.Id)""
    }} else {{
        Write-Host ""ERROR: Failed to import audio file - no result returned""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to import audio file: $_""
}}";
        }

        public string GetAssignLicenseCommand(string userId, string skuId)
        {
            // SECURITY: Sanitize inputs
            var sanitizedUserId = _sanitizer.SanitizeString(userId);
            var sanitizedSkuId = _sanitizer.SanitizeString(skuId);
            
            return $@"
try {{
    $SkuId = ""{sanitizedSkuId}""
    Set-MgUserLicense -UserId ""{sanitizedUserId}"" -AddLicenses @{{SkuId = $SkuId}} -RemoveLicenses @()
    Write-Host ""SUCCESS: License assigned successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to assign license: $_""
}}";
        }

        public string GetCreateM365GroupCommand(string groupName)
        {
            var sanitizedGroupName = _sanitizer.SanitizeString(groupName);

            return $@"
$existingGroup = Get-MgGroup -Filter ""displayName eq '{sanitizedGroupName}'"" -ErrorAction SilentlyContinue
if ($existingGroup)
{{
    Write-Host ""{sanitizedGroupName} already exists. Please check, otherwise SFO will be salty!""
    $global:m365groupId = $existingGroup.Id
    Write-Host ""{sanitizedGroupName} was found successfully with ID: $global:m365groupId""
    return
}}

try {{
    $newGroup = New-MgGroup -DisplayName ""{sanitizedGroupName}"" `
        -MailEnabled:$True `
        -MailNickName ""{sanitizedGroupName}"" `
        -SecurityEnabled `
        -GroupTypes @(""Unified"")

    $global:m365groupId = $newGroup.Id
    Write-Host ""{sanitizedGroupName} created successfully with ID: $global:m365groupId""
}}
catch {{
    Write-Host ""ERROR: {sanitizedGroupName} failed to create: $_""
}}";
        }

        public string GetRemoveM365GroupCommand(string groupId, string groupName)
        {
            var sanitizedId = _sanitizer.SanitizeString(groupId);
            var sanitizedName = _sanitizer.SanitizeString(groupName);

            return $@"
try {{
    $group = Get-MgGroup -GroupId ""{sanitizedId}"" -ErrorAction Stop
    if ($group) {{
        Remove-MgGroup -GroupId ""{sanitizedId}"" -ErrorAction Stop
        Write-Host ""SUCCESS: M365 Group '{sanitizedName}' (ID: {sanitizedId}) removed successfully""
    }} else {{
        Write-Host ""ERROR: M365 Group with ID '{sanitizedId}' not found""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to remove M365 Group '{sanitizedName}': $_""
}}";
        }

        public string GetRetrieveM365GroupsCommand()
        {
            return @"
try {
    $groups = Get-MgGroup -Filter ""startswith(displayName,'" + ConstantsService.Naming.M365GroupPrefix + @"')"" -All
    if ($groups) {
        Write-Host ""SUCCESS: Found $($groups.Count) groups starting with '" + ConstantsService.Naming.M365GroupPrefix + @"'""
        foreach ($group in $groups) {
            Write-Host ""GROUP: $($group.DisplayName)|$($group.Id)|$($group.MailNickname)|$($group.Description)""
        }
    } else {
        Write-Host ""INFO: No groups found starting with '" + ConstantsService.Naming.M365GroupPrefix + @"'""
    }
}
catch {
    Write-Host ""ERROR: Failed to retrieve groups: $_""
}";
        }

        public string GetM365GroupIdCommand(string groupName)
        {
            var sanitizedGroupName = _sanitizer.SanitizeString(groupName);
            
            return $@"
try {{
    $group = Get-MgGroup -Filter ""DisplayName eq '{sanitizedGroupName}'""
    if ($group) {{
        Write-Host ""SUCCESS: M365 Group ID retrieved successfully""
        Write-Host ""M365GROUPID: $($group.Id)""
    }} else {{
        Write-Host ""ERROR: M365 Group not found""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to retrieve M365 Group ID: $_""
}}";
        }
    }
}
