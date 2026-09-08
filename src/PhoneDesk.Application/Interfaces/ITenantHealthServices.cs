using PhoneDesk.HealthChecks;

namespace PhoneDesk.Services.Interfaces;

public interface ITenantHealthEvaluationService
{
    IReadOnlyList<TenantHealthFinding> Evaluate(
        TenantHealthContext context,
        IReadOnlySet<string> enabledRuleIds);
}

public interface ITenantHealthEnrichmentParser
{
    TenantHealthEnrichment Parse(string rawOutput);
}

public interface ITenantHealthQueryBuilder
{
    string GetTenantHealthEnrichmentCommand();
}

public interface ITenantHealthPreferencesStore
{
    TenantHealthPreferences Load(string tenantId);
    bool Save(string tenantId, TenantHealthPreferences preferences);
}

public interface ITenantHealthCheckCache
{
    TenantHealthCheckResult? Current { get; }
    void Set(TenantHealthCheckResult result);
    void Clear();
}
