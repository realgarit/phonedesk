using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

/// <summary>
/// Builds the dedicated read-only enrichment query for tenant health checks. It is deliberately
/// separate from the frozen ScriptBuilders surface and emits only HEALTH_* marker rows consumed by
/// the Application parser.
/// </summary>
public sealed class TenantHealthQueryBuilder : ITenantHealthQueryBuilder
{
    public string GetTenantHealthEnrichmentCommand()
    {
        return @"
$ErrorActionPreference = 'Stop'

try {
    $resourceSkuIds = @(
        Get-MgSubscribedSku -All -ErrorAction Stop |
            Where-Object { $_.SkuPartNumber -eq 'PHONESYSTEM_VIRTUALUSER' } |
            ForEach-Object { [string]$_.SkuId }
    )

    $resourceAccounts = @(Get-CsOnlineApplicationInstance -ErrorAction Stop)
    Write-Host ""HEALTH_RA_START""
    foreach ($account in $resourceAccounts) {
        $user = Get-MgUser -UserId $account.ObjectId -Property Id,AssignedLicenses,UsageLocation -ErrorAction Stop
        $assignedSkuIds = @($user.AssignedLicenses | ForEach-Object { [string]$_.SkuId })
        $hasResourceLicense = @($assignedSkuIds | Where-Object { $resourceSkuIds -contains $_ }).Count -gt 0
        $usageLocation = if ($user.UsageLocation) { [string]$user.UsageLocation } else { '' }
        Write-Host (""HEALTH_RA: {0}|{1}|{2}"" -f $account.ObjectId, $hasResourceLicense, $usageLocation)
    }
    Write-Host ""HEALTH_RA_END""

    $autoAttendants = @(Get-CsAutoAttendant -ErrorAction Stop)
    Write-Host ""HEALTH_AA_START""
    foreach ($attendant in $autoAttendants) {
        $hasAfterHours = @(
            $attendant.CallHandlingAssociations |
                Where-Object { $_.Type -eq 'AfterHours' }
        ).Count -gt 0
        Write-Host (""HEALTH_AA: {0}|{1}"" -f $attendant.Identity, $hasAfterHours)
    }
    Write-Host ""HEALTH_AA_END""

    $serviceNumbers = @(
        Get-CsPhoneNumberAssignment `
            -PstnAssignmentStatus Unassigned `
            -CapabilitiesContain VoiceApplicationAssignment `
            -ErrorAction Stop
    )
    Write-Host ""HEALTH_PHONE_START""
    foreach ($number in $serviceNumbers) {
        Write-Host (""HEALTH_PHONE: {0}|{1}"" -f $number.TelephoneNumber, $number.NumberType)
    }
    Write-Host ""HEALTH_PHONE_END""

    Write-Host ""SUCCESS: Tenant health enrichment retrieved""
}
catch {
    Write-Host ""ERROR: Tenant health enrichment failed: $_""
}
";
    }
}
