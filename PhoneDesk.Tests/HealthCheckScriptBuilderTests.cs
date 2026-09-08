using System.Text.RegularExpressions;
using PhoneDesk.Services;

namespace PhoneDesk.Tests;

public sealed class HealthCheckScriptBuilderTests
{
    [Fact]
    public void EnrichmentScriptIsReadOnlyAndUsesDocumentedLicenseAndServiceNumberQueries()
    {
        var script = new TenantHealthQueryBuilder().GetTenantHealthEnrichmentCommand();

        Assert.Contains("Get-MgSubscribedSku -All", script, StringComparison.Ordinal);
        Assert.Contains("PHONESYSTEM_VIRTUALUSER", script, StringComparison.Ordinal);
        Assert.Contains("Get-MgUser -UserId", script, StringComparison.Ordinal);
        Assert.Contains("Get-CsAutoAttendant", script, StringComparison.Ordinal);
        Assert.Contains("-PstnAssignmentStatus Unassigned", script, StringComparison.Ordinal);
        Assert.Contains("-CapabilitiesContain VoiceApplicationAssignment", script, StringComparison.Ordinal);
        Assert.Contains("SUCCESS: Tenant health enrichment retrieved", script, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(
            script,
            @"(?im)^\s*(Set|New|Remove|Update)-[A-Za-z]",
            RegexOptions.CultureInvariant));
    }
}
