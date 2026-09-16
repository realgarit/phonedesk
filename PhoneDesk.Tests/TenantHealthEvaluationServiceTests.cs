using PhoneDesk.HealthChecks;
using PhoneDesk.Services;
using PhoneDesk.Topology;

namespace PhoneDesk.Tests;

public sealed class TenantHealthEvaluationServiceTests
{
    private readonly TenantHealthEvaluationService _service = new();

    [Fact]
    public void DefaultCatalogCoversEveryInitialRuleWithUniqueIdsAndBilingualText()
    {
        Assert.Equal(6, TenantHealthRuleCatalog.Rules.Count);
        Assert.Equal(
            TenantHealthRuleCatalog.Rules.Count,
            TenantHealthRuleCatalog.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(TenantHealthRuleCatalog.Rules, rule =>
        {
            Assert.True(rule.EnabledByDefault);
            Assert.False(string.IsNullOrWhiteSpace(rule.Title.English));
            Assert.False(string.IsNullOrWhiteSpace(rule.Title.German));
        });
    }

    [Fact]
    public void AllRulesProduceSeverityExplanationRecommendationAndSupportedDeepLinks()
    {
        var context = CreateContext();
        var enabled = TenantHealthRuleCatalog.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);

        var findings = _service.Evaluate(context, enabled);

        Assert.Equal(7, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.FindingId));
            Assert.False(string.IsNullOrWhiteSpace(finding.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation.English));
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation.German));
            Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation.English));
            Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation.German));
        });
        Assert.Contains(findings, finding =>
            finding.RuleId == TenantHealthRuleCatalog.ResourceAccountLicense &&
            finding.ObjectId == "ra-missing-license" &&
            finding.Severity == TenantHealthSeverity.Error &&
            finding.DestinationPage == ConstantsService.Pages.AutoAttendants);
        Assert.Contains(findings, finding =>
            finding.RuleId == TenantHealthRuleCatalog.CallQueueWithoutAgents &&
            finding.ObjectId == "cq-empty" &&
            finding.Severity == TenantHealthSeverity.Error &&
            finding.DestinationPage == ConstantsService.Pages.CallQueues);
        Assert.Contains(findings, finding =>
            finding.RuleId == TenantHealthRuleCatalog.AutoAttendantWithoutAfterHours &&
            finding.ObjectId == "aa-no-hours" &&
            finding.DestinationPage == ConstantsService.Pages.AutoAttendants);
        Assert.Contains(findings, finding =>
            finding.RuleId == TenantHealthRuleCatalog.UnassignedServiceNumber &&
            finding.ObjectId == "+41440000099" &&
            finding.DestinationPage is null);
    }

    [Fact]
    public void DisabledRulesAreNotEvaluatedAndFindingIdsAreStable()
    {
        var context = CreateContext();
        var enabled = new HashSet<string>(StringComparer.Ordinal)
        {
            TenantHealthRuleCatalog.CallQueueWithoutAgents,
        };

        var first = _service.Evaluate(context, enabled);
        var second = _service.Evaluate(context, enabled);

        var finding = Assert.Single(first);
        Assert.Equal(TenantHealthRuleCatalog.CallQueueWithoutAgents, finding.RuleId);
        Assert.Equal(first.Select(item => item.FindingId), second.Select(item => item.FindingId));
    }

    [Fact]
    public void MissingEnrichmentFailsClosedBeforeRulesRun()
    {
        var context = CreateContext() with
        {
            Enrichment = new TenantHealthEnrichment(
                Array.Empty<ResourceAccountHealthState>(),
                Array.Empty<AutoAttendantHealthState>(),
                Array.Empty<UnassignedServiceNumberHealthState>())
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            _service.Evaluate(
                context,
                TenantHealthRuleCatalog.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal)));

        Assert.Contains("missing resource account", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static TenantHealthContext CreateContext()
    {
        var topology = new TenantTopology(
            new[]
            {
                new TopologyAutoAttendant(
                    "Main reception", "aa-no-hours", "de-DE", "Europe/Zurich",
                    new[] { "ra-missing-license" }, Array.Empty<string>(), Array.Empty<string>()),
            },
            new[]
            {
                new TopologyCallQueue(
                    "Empty support", "cq-empty", "LongestIdle", 20,
                    Array.Empty<string>(), Array.Empty<string>(), new[] { "ra-queue" }),
            },
            new[]
            {
                new TopologyResourceAccount(
                    "Reception RA", "reception@contoso.example", "ra-missing-license", null,
                    ResourceAccountKind.AutoAttendant, true),
                new TopologyResourceAccount(
                    "Queue RA", "queue@contoso.example", "ra-queue", "+41440000001",
                    ResourceAccountKind.CallQueue, true),
                new TopologyResourceAccount(
                    "Unused RA", "unused@contoso.example", "ra-unused", null,
                    ResourceAccountKind.CallQueue, true),
            },
            Array.Empty<TopologyGroup>(),
            Array.Empty<OrphanFinding>(),
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var enrichment = new TenantHealthEnrichment(
            new[]
            {
                new ResourceAccountHealthState("ra-missing-license", false, string.Empty),
                new ResourceAccountHealthState("ra-queue", true, "CH"),
                new ResourceAccountHealthState("ra-unused", true, "CH"),
            },
            new[]
            {
                new AutoAttendantHealthState("aa-no-hours", false),
            },
            new[]
            {
                new UnassignedServiceNumberHealthState("+41440000099", "CallingPlan"),
            });
        return new TenantHealthContext("tenant-1", topology, enrichment);
    }
}
