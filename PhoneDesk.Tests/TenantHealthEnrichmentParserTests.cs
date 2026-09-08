using PhoneDesk.Services;

namespace PhoneDesk.Tests;

public sealed class TenantHealthEnrichmentParserTests
{
    private readonly TenantHealthEnrichmentParser _parser = new();

    [Fact]
    public void ParseReturnsStableTypedEnrichment()
    {
        var result = _parser.Parse(CompleteOutput);

        Assert.Equal("ra-1", Assert.Single(result.ResourceAccounts).ObjectId);
        Assert.True(result.ResourceAccounts[0].HasResourceAccountLicense);
        Assert.Equal("CH", result.ResourceAccounts[0].UsageLocation);
        Assert.True(Assert.Single(result.AutoAttendants).HasAfterHoursHandling);
        Assert.Equal("+41440000099", Assert.Single(result.UnassignedServiceNumbers).TelephoneNumber);
    }

    [Fact]
    public void ParseRejectsMissingMarkersInvalidBooleansAndDuplicateIds()
    {
        Assert.Throws<InvalidDataException>(() =>
            _parser.Parse(CompleteOutput.Replace("HEALTH_RA_END", "", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            _parser.Parse(CompleteOutput.Replace("ra-1|True|CH", "ra-1|unknown|CH", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            _parser.Parse(CompleteOutput.Replace(
                "HEALTH_RA_END",
                "HEALTH_RA: ra-1|True|CH\nHEALTH_RA_END",
                StringComparison.Ordinal)));
    }

    internal const string CompleteOutput =
        "HEALTH_RA_START\n" +
        "HEALTH_RA: ra-1|True|CH\n" +
        "HEALTH_RA_END\n" +
        "HEALTH_AA_START\n" +
        "HEALTH_AA: aa-1|True\n" +
        "HEALTH_AA_END\n" +
        "HEALTH_PHONE_START\n" +
        "HEALTH_PHONE: +41440000099|CallingPlan\n" +
        "HEALTH_PHONE_END\n" +
        "SUCCESS: Tenant health enrichment retrieved";
}
