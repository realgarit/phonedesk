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
    private readonly McpEnvironment _environment;

    public HealthAndBackupTools(
        TenantGateway tenant,
        TenantHealthCheckService healthCheck,
        ITenantAsCodeService tenantAsCode,
        CreatedObjectLedger ledger,
        McpEnvironment environment)
    {
        _environment = environment;
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
        [Description("The findingId of a finding from run_health_check, exactly as given there.")] string findingId,
        [Description("True to hide the finding, false to show it again.")] bool suppressed)
        => McpJson.Run(() =>
        {
            _tenant.RequireReady();

            // Only a finding of the last check can be hidden or shown: any other text (a rule ID, an
            // object name) would be saved, report success and hide nothing.
            var id = (findingId ?? string.Empty).Trim();
            var last = _healthCheck.ReevaluateLastRun()
                ?? throw new InvalidOperationException("There is no health check of this session yet. Call run_health_check first and use a findingId from its result.");
            var known = (suppressed ? last.ActiveFindings.Concat(last.SuppressedFindings) : last.SuppressedFindings)
                .Any(finding => string.Equals(finding.FindingId, id, StringComparison.Ordinal));
            if (!known)
            {
                throw new ArgumentException(suppressed
                    ? $"'{id}' is not a finding of the last health check. Use a findingId from run_health_check (not a rule ID or an object name)."
                    : $"'{id}' is not a hidden finding of the last health check. See suppressedFindings in run_health_check.");
            }

            findingId = id;
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

    [McpServerTool(Name = "list_backups", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the snapshot files of the connected tenant on this computer, newest first, with the time each one was read from the tenant. Use a 'file' value with compare_with_backup to compare the tenant with an older state, for example the one from before yesterday's changes. Reads local files only.")]
    public string ListBackups() => McpJson.Run(() =>
    {
        _tenant.RequireReady();
        var preChangeBackup = _tenant.PreChangeBackupFile;
        return new
        {
            tenantId = _tenant.TenantId,
            backups = _tenant.ListBackupFiles().Select(file => new
            {
                file,
                takenUtc = BackupTime(file),
                sizeBytes = new FileInfo(file).Length,
                writtenBeforeTheFirstChangeOfThisSession = string.Equals(file, preChangeBackup, StringComparison.OrdinalIgnoreCase)
            })
        };
    });

    [McpServerTool(Name = "export_tenant_report", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes the readable documentation of a tenant from a snapshot file: report.html (one self-contained file with a section per kind of object, links between related objects, and a call-flow diagram per auto attendant), report.xlsx (one sheet per kind of object, with filters), and one call-flow diagram per auto attendant as SVG. It is built from the snapshot alone: the tenant is not read, so take a snapshot with backup_tenant first when you want the current state. Without a file it uses the newest backup of the connected tenant. The files go into a new folder under 'reports' in the data folder of this server; nothing is overwritten, and the same snapshot gives the same files. The result lists the files; tell the user where they are. The desktop app writes the same report and also the diagrams as PNG pictures.")]
    public string ExportTenantReport(
        [Description("Full path of a snapshot file, from list_backups or backup_tenant. Omit to use the newest backup of the connected tenant.")] string? file = null,
        [Description("Language of the headings: en or de.")] string language = "en")
        => McpJson.Run(() =>
        {
            var path = string.IsNullOrWhiteSpace(file) ? _tenant.FindLatestBackupFile() : file;
            if (path is null)
            {
                throw new InvalidOperationException("There is no backup for this tenant yet. Run backup_tenant first, or name a snapshot file.");
            }

            if (!string.Equals(language, RichReport.English, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(language, RichReport.German, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The language is en or de.");
            }

            var snapshot = _tenantAsCode.DeserializeTopology(McpFiles.ReadJsonFile(path, maxBytes: 64 * 1024 * 1024));
            var written = RichReport.WriteFiles(Path.Combine(_environment.DataDirectory, "reports"), snapshot, language.ToLowerInvariant());
            return new
            {
                success = true,
                snapshotFile = Path.GetFullPath(path),
                snapshotTakenUtc = snapshot.RetrievedAtUtc,
                folder = written.Directory,
                files = written.Files,
                autoAttendantDiagrams = written.Files.Count(name => name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)),
                note = "The report shows the tenant at the time of the snapshot."
            };
        });

    [McpServerTool(Name = "compare_with_backup", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the live tenant again and compares it with a snapshot file: lists every object and property that was added, removed or changed since the snapshot. Without a file it uses the newest backup of the connected tenant, which can be one this session wrote; list_backups shows the older ones. Read-only.")]
    public Task<string> CompareWithBackup(
        [Description("Full path of a snapshot file, from list_backups. Omit to use the newest backup of this tenant.")] string? file = null,
        [Description("Maximum number of differences to return (default 100, at most 1000).")] int limit = 100,
        [Description("Number of differences to skip, to read the next part of a long list.")] int offset = 0,
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
            var snapshot = _tenantAsCode.DeserializeTopology(McpFiles.ReadJsonFile(path, maxBytes: 64 * 1024 * 1024));
            var live = await _tenant.CollectSnapshotAsync(cancellationToken);
            var drift = _tenantAsCode.CompareTopology(snapshot, live);
            var take = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
            var skip = Math.Max(0, offset);
            return new
            {
                comparedFile = path,
                snapshotTakenUtc = snapshot.RetrievedAtUtc,
                liveReadUtc = live.RetrievedAtUtc,
                differenceCount = drift.Count,
                offset = skip,
                truncated = drift.Count > skip + take,
                nextOffset = drift.Count > skip + take ? skip + take : (int?)null,
                differences = drift.Skip(skip).Take(take)
            };
        });

    [McpServerTool(Name = "created_objects", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the objects this server created in the connected tenant. The delete tools accept only these objects.")]
    public string CreatedObjects() => McpJson.Run(() =>
    {
        _tenant.RequireReady();
        return new { tenantId = _tenant.TenantId, objects = _ledger.List(_tenant.TenantId) };
    });

    /// <summary>The UTC time in a backup file name (phonedesk-topology-yyyyMMddTHHmmssZ.json), or null for another name.</summary>
    private static DateTimeOffset? BackupTime(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        const string prefix = "phonedesk-topology-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && DateTimeOffset.TryParseExact(
            name[prefix.Length..], "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
    }

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
