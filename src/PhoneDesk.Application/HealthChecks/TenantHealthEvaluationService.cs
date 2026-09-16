using PhoneDesk.HealthChecks;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class TenantHealthEvaluationService : ITenantHealthEvaluationService
{
    public IReadOnlyList<TenantHealthFinding> Evaluate(
        TenantHealthContext context,
        IReadOnlySet<string> enabledRuleIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(enabledRuleIds);
        ValidateCoverage(context);

        return TenantHealthRuleCatalog.Rules
            .Where(rule => enabledRuleIds.Contains(rule.Id))
            .SelectMany(rule => rule.Evaluate(context))
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.FindingId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateCoverage(TenantHealthContext context)
    {
        if (string.IsNullOrWhiteSpace(context.TenantId))
        {
            throw new InvalidDataException("A tenant ID is required for health checks.");
        }

        var resourceAccountIds = context.Enrichment.ResourceAccounts
            .Select(item => item.ObjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingResourceAccount = context.Topology.ResourceAccounts
            .FirstOrDefault(item => !resourceAccountIds.Contains(item.ObjectId));
        if (missingResourceAccount is not null)
        {
            throw new InvalidDataException(
                $"Health-check enrichment is missing resource account '{missingResourceAccount.ObjectId}'.");
        }

        var autoAttendantIds = context.Enrichment.AutoAttendants
            .Select(item => item.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingAutoAttendant = context.Topology.AutoAttendants
            .FirstOrDefault(item => !autoAttendantIds.Contains(item.Identity));
        if (missingAutoAttendant is not null)
        {
            throw new InvalidDataException(
                $"Health-check enrichment is missing auto attendant '{missingAutoAttendant.Identity}'.");
        }
    }
}
