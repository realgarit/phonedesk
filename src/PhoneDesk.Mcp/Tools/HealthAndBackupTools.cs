using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.HealthChecks;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class HealthAndBackupTools
{
    private readonly TenantGateway _tenant;
    private readonly TenantHealthCheckService _healthCheck;
    private readonly ITenantAsCodeService _tenantAsCode;
    private readonly CreatedObjectLedger _ledger;

    public HealthAndBackupTools(
        TenantGateway tenant,
        TenantHealthCheckService healthCheck,
        ITenantAsCodeService tenantAsCode,
        CreatedObjectLedger ledger)
    {
        _tenant = tenant;
        _healthCheck = healthCheck;
        _tenantAsCode = tenantAsCode;
        _ledger = ledger;
    }

    [McpServerTool(Name = "run_health_check", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Checks the tenant's phone system against the PhoneDesk rule catalog (for example resource accounts without a license or usage location, auto attendants without after-hours handling, unused service numbers) and returns the findings with an explanation and a recommendation. Read-only.")]
    public Task<string> RunHealthCheck(
        [Description("True to read the tenant topology again before the check.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return Describe(await _healthCheck.RunAsync(topology, cancellationToken));
        });

    [McpServerTool(Name = "health_rules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the health-check rules and whether each one is switched on for the connected tenant.")]
    public string HealthRules() => McpJson.Run(() =>
    {
        _tenant.RequireReady();
        return new { rules = _healthCheck.GetRules() };
    });

    [McpServerTool(Name = "health_rule_set", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Switches one health-check rule on or off for the connected tenant. This is a local preference shared with the desktop app; the tenant is not touched.")]
    public string SetHealthRule(
        [Description("Rule ID from health_rules.")] string ruleId,
        [Description("True to switch the rule on.")] bool enabled)
        => McpJson.Run(() =>
        {
            _tenant.RequireReady();
            if (!_healthCheck.SetRuleEnabled(ruleId, enabled))
            {
                throw new InvalidOperationException($"Rule '{ruleId}' is unknown or the preference could not be saved.");
            }

            return new { success = true, rules = _healthCheck.GetRules(), lastRun = DescribeOrNull(_healthCheck.ReevaluateLastRun()) };
        });

    [McpServerTool(Name = "health_finding_set_suppressed", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Hides one health-check finding for the connected tenant, or shows it again. Use it for a finding the operator has accepted. This is a local preference shared with the desktop app; the tenant is not touched.")]
    public string SetFindingSuppressed(
        [Description("Finding ID from run_health_check.")] string findingId,
        [Description("True to hide the finding, false to show it again.")] bool suppressed)
        => McpJson.Run(() =>
        {
            _tenant.RequireReady();
            if (!_healthCheck.SetFindingSuppressed(findingId, suppressed))
            {
                throw new InvalidOperationException("The preference could not be saved.");
            }

            return new { success = true, lastRun = DescribeOrNull(_healthCheck.ReevaluateLastRun()) };
        });

    [McpServerTool(Name = "backup_tenant", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the complete phone system of the tenant (topology, call flows, schedules, agents, phone numbers, voice users) and writes it to a snapshot file on this computer. Only reads from the tenant. The snapshot is a record for comparison and manual restore; it cannot be restored automatically. A backup is also taken automatically before the first change of a session.")]
    public Task<string> BackupTenant(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => new { success = true, backup = await _tenant.BackupAsync(cancellationToken) });

    [McpServerTool(Name = "compare_with_backup", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the live tenant again and compares it with a snapshot file: lists every object and property that was added, removed or changed since the snapshot. Without a file it uses the newest backup of the connected tenant. Read-only.")]
    public Task<string> CompareWithBackup(
        [Description("Full path of a snapshot file. Omit to use the newest backup of this tenant.")] string? file = null,
        [Description("Maximum number of differences to return (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            _tenant.RequireReady();
            var path = string.IsNullOrWhiteSpace(file) ? _tenant.FindLatestBackupFile() : Path.GetFullPath(file);
            if (path is null)
            {
                throw new InvalidOperationException("There is no backup for this tenant yet. Run backup_tenant first.");
            }

            // The file is validated before the tenant is queried.
            var snapshot = _tenantAsCode.DeserializeTopology(await File.ReadAllTextAsync(path, cancellationToken));
            var live = await _tenant.CollectSnapshotAsync(cancellationToken);
            var drift = _tenantAsCode.CompareTopology(snapshot, live);
            var take = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
            return new
            {
                comparedFile = path,
                snapshotTakenUtc = snapshot.RetrievedAtUtc,
                liveReadUtc = live.RetrievedAtUtc,
                differenceCount = drift.Count,
                truncated = drift.Count > take,
                differences = drift.Take(take)
            };
        });

    [McpServerTool(Name = "created_objects", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the objects this server created in the connected tenant. The delete tools accept only these objects.")]
    public string CreatedObjects() => McpJson.Run(() =>
    {
        _tenant.RequireReady();
        return new { tenantId = _tenant.TenantId, objects = _ledger.List(_tenant.TenantId) };
    });

    private static object? DescribeOrNull(HealthCheckReport? report) => report is null ? null : Describe(report);

    private static object Describe(HealthCheckReport report) => new
    {
        tenantId = report.TenantId,
        evaluatedAtUtc = report.EvaluatedAtUtc,
        activeFindingCount = report.ActiveFindings.Count,
        suppressedFindingCount = report.SuppressedFindings.Count,
        disabledRules = report.Rules.Where(rule => !rule.Enabled).Select(rule => rule.RuleId),
        activeFindings = report.ActiveFindings.Select(DescribeFinding),
        suppressedFindings = report.SuppressedFindings.Select(DescribeFinding)
    };

    private static object DescribeFinding(TenantHealthFinding finding) => new
    {
        findingId = finding.FindingId,
        ruleId = finding.RuleId,
        severity = finding.Severity,
        objectType = finding.ObjectType,
        objectId = finding.ObjectId,
        displayName = finding.DisplayName,
        explanation = finding.Explanation.English,
        recommendation = finding.Recommendation.English
    };
}
