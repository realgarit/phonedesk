using PhoneDesk.Topology;

namespace PhoneDesk.HealthChecks;

public enum TenantHealthSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public sealed record LocalizedHealthText(string English, string German);

public sealed record ResourceAccountHealthState(
    string ObjectId,
    bool HasResourceAccountLicense,
    string UsageLocation);

public sealed record AutoAttendantHealthState(
    string Identity,
    bool HasAfterHoursHandling);

public sealed record UnassignedServiceNumberHealthState(
    string TelephoneNumber,
    string NumberType);

public sealed record TenantHealthEnrichment(
    IReadOnlyList<ResourceAccountHealthState> ResourceAccounts,
    IReadOnlyList<AutoAttendantHealthState> AutoAttendants,
    IReadOnlyList<UnassignedServiceNumberHealthState> UnassignedServiceNumbers);

public sealed record TenantHealthContext(
    string TenantId,
    TenantTopology Topology,
    TenantHealthEnrichment Enrichment);

public sealed record TenantHealthFinding(
    string FindingId,
    string RuleId,
    TenantHealthSeverity Severity,
    string ObjectType,
    string ObjectId,
    string DisplayName,
    LocalizedHealthText Explanation,
    LocalizedHealthText Recommendation,
    string? DestinationPage);

public sealed record TenantHealthRuleDefinition(
    string Id,
    bool EnabledByDefault,
    LocalizedHealthText Title,
    Func<TenantHealthContext, IEnumerable<TenantHealthFinding>> Evaluate);

public sealed record TenantHealthPreferences(
    IReadOnlySet<string> DisabledRuleIds,
    IReadOnlySet<string> SuppressedFindingIds)
{
    public static TenantHealthPreferences Default { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public sealed record TenantHealthCheckResult(
    string TenantId,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<TenantHealthFinding> ActiveFindings,
    int SuppressedCount,
    int DisabledRuleCount);
