using PhoneDesk.HealthChecks;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Operations;

public sealed record HealthRuleState(string RuleId, string Title, bool Enabled);

public sealed record HealthCheckReport(
    string TenantId,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<TenantHealthFinding> ActiveFindings,
    IReadOnlyList<TenantHealthFinding> SuppressedFindings,
    IReadOnlyList<HealthRuleState> Rules);

/// <summary>
/// Runs the tenant health check for a head without a UI: one read-only enrichment query, then the
/// shared rule catalog, with the per-tenant rule switches and suppressed findings the desktop app
/// also uses.
/// </summary>
public sealed class TenantHealthCheckService
{
    private readonly ITenantCommandRunner _runner;
    private readonly ISessionManager _session;
    private readonly ITenantHealthQueryBuilder _queryBuilder;
    private readonly ITenantHealthEnrichmentParser _enrichmentParser;
    private readonly ITenantHealthEvaluationService _evaluation;
    private readonly ITenantHealthPreferencesStore _preferencesStore;
    private readonly ITenantHealthCheckCache _cache;

    private TenantHealthContext? _lastContext;
    private DateTimeOffset _lastRunUtc;

    public TenantHealthCheckService(
        ITenantCommandRunner runner,
        ISessionManager session,
        ITenantHealthQueryBuilder queryBuilder,
        ITenantHealthEnrichmentParser enrichmentParser,
        ITenantHealthEvaluationService evaluation,
        ITenantHealthPreferencesStore preferencesStore,
        ITenantHealthCheckCache cache)
    {
        _runner = runner;
        _session = session;
        _queryBuilder = queryBuilder;
        _enrichmentParser = enrichmentParser;
        _evaluation = evaluation;
        _preferencesStore = preferencesStore;
        _cache = cache;
    }

    /// <exception cref="InvalidDataException">The health-check query did not complete.</exception>
    public async Task<HealthCheckReport> RunAsync(TenantTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var tenantId = RequireTenantId();

        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(_queryBuilder.GetTenantHealthEnrichmentCommand(), "TenantHealthCheck")
            {
                IsIdempotentRead = true
            },
            cancellationToken);
        if (!outcome.Result.IsSuccess)
        {
            throw new InvalidDataException(
                outcome.Result.ErrorMessage ?? "The health-check query did not complete successfully.");
        }

        _lastContext = new TenantHealthContext(
            tenantId, topology, _enrichmentParser.Parse(outcome.Result.Value ?? string.Empty));
        _lastRunUtc = DateTimeOffset.UtcNow;
        return Evaluate(_lastContext);
    }

    /// <summary>Returns the rule switches for the connected tenant.</summary>
    public IReadOnlyList<HealthRuleState> GetRules() => BuildRules(_preferencesStore.Load(RequireTenantId()));

    /// <summary>Turns a rule on or off for the connected tenant. Returns false when the rule is unknown or the save failed.</summary>
    public bool SetRuleEnabled(string ruleId, bool enabled)
    {
        if (!TenantHealthRuleCatalog.Rules.Any(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal)))
        {
            return false;
        }

        var tenantId = RequireTenantId();
        var preferences = _preferencesStore.Load(tenantId);
        var disabled = preferences.DisabledRuleIds.ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            disabled.Remove(ruleId);
        }
        else
        {
            disabled.Add(ruleId);
        }

        return _preferencesStore.Save(tenantId, preferences with { DisabledRuleIds = disabled });
    }

    /// <summary>Hides or restores one finding for the connected tenant. Returns false when the save failed.</summary>
    public bool SetFindingSuppressed(string findingId, bool suppressed)
    {
        var tenantId = RequireTenantId();
        var preferences = _preferencesStore.Load(tenantId);
        var ids = preferences.SuppressedFindingIds.ToHashSet(StringComparer.Ordinal);
        if (suppressed)
        {
            ids.Add(findingId);
        }
        else
        {
            ids.Remove(findingId);
        }

        return _preferencesStore.Save(tenantId, preferences with { SuppressedFindingIds = ids });
    }

    /// <summary>Evaluates the last health-check data again with the current switches. Null before the first run.</summary>
    public HealthCheckReport? ReevaluateLastRun()
        => _lastContext is not null &&
           string.Equals(_lastContext.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase)
            ? Evaluate(_lastContext)
            : null;

    private HealthCheckReport Evaluate(TenantHealthContext context)
    {
        var preferences = _preferencesStore.Load(context.TenantId);
        var rules = BuildRules(preferences);
        var enabledRuleIds = rules
            .Where(rule => rule.Enabled)
            .Select(rule => rule.RuleId)
            .ToHashSet(StringComparer.Ordinal);
        var findings = _evaluation.Evaluate(context, enabledRuleIds);
        var active = findings.Where(finding => !preferences.SuppressedFindingIds.Contains(finding.FindingId)).ToArray();
        var suppressed = findings.Where(finding => preferences.SuppressedFindingIds.Contains(finding.FindingId)).ToArray();

        _cache.Set(new TenantHealthCheckResult(
            context.TenantId,
            _lastRunUtc,
            active,
            suppressed.Length,
            TenantHealthRuleCatalog.Rules.Count - enabledRuleIds.Count));

        return new HealthCheckReport(context.TenantId, _lastRunUtc, active, suppressed, rules);
    }

    private static IReadOnlyList<HealthRuleState> BuildRules(TenantHealthPreferences preferences)
        => TenantHealthRuleCatalog.Rules
            .Select(rule => new HealthRuleState(
                rule.Id, rule.Title.English, !preferences.DisabledRuleIds.Contains(rule.Id)))
            .ToArray();

    private string RequireTenantId()
        => string.IsNullOrWhiteSpace(_session.TenantId)
            ? throw new InvalidOperationException(
                "Connect to Microsoft Teams and Microsoft Graph so the tenant can be identified.")
            : _session.TenantId;
}
