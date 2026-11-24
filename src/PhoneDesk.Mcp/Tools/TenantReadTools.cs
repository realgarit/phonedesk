using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class TenantReadTools
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private readonly TenantGateway _tenant;
    private readonly ISessionManager _session;
    private readonly ITenantAsCodeService _tenantAsCode;
    private readonly IAuditLog _auditLog;

    public TenantReadTools(
        TenantGateway tenant,
        ISessionManager session,
        ITenantAsCodeService tenantAsCode,
        IAuditLog auditLog)
    {
        _tenant = tenant;
        _session = session;
        _tenantAsCode = tenantAsCode;
        _auditLog = auditLog;
    }

    [McpServerTool(Name = "tenant_overview", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the tenant's phone system and returns counts plus the objects that are not linked correctly (for example a resource account without an auto attendant or call queue, or a call queue without agents). Read-only. The result is cached for the session; pass refresh=true after something changed.")]
    public Task<string> Overview(
        [Description("True to query the tenant again instead of using the cached result.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return new
            {
                tenant = new { name = _session.TenantName, id = _session.TenantId },
                retrievedAtUtc = topology.RetrievedAtUtc,
                counts = new
                {
                    autoAttendants = topology.AutoAttendants.Count,
                    callQueues = topology.CallQueues.Count,
                    resourceAccounts = topology.ResourceAccounts.Count,
                    m365Groups = topology.Groups.Count,
                    orphans = topology.Orphans.Count
                },
                orphans = topology.Orphans
            };
        });

    [McpServerTool(Name = "list_auto_attendants", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists auto attendants with language, time zone, their resource accounts and the number of holiday schedules. Read-only.")]
    public Task<string> ListAutoAttendants(
        [Description("Only names that contain this text.")] string? search = null,
        [Description("Maximum number of rows (default 50).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            var accounts = topology.ResourceAccounts.ToLookup(account => account.ObjectId, StringComparer.OrdinalIgnoreCase);
            return Page(
                topology.AutoAttendants.Where(item => Contains(item.Name, search)),
                limit,
                item => new
                {
                    name = item.Name,
                    identity = item.Identity,
                    languageId = item.LanguageId,
                    timeZoneId = item.TimeZoneId,
                    resourceAccounts = item.ResourceAccountObjectIds.Select(id => DescribeAccount(accounts, id)),
                    holidayScheduleCount = item.HolidayScheduleIds.Count,
                    callTargetCount = item.CallTargetObjectIds.Count
                });
        });

    [McpServerTool(Name = "list_call_queues", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists call queues with routing method, agent alert time, the number of direct agents, their agent groups and their resource accounts. Read-only.")]
    public Task<string> ListCallQueues(
        [Description("Only names that contain this text.")] string? search = null,
        [Description("Maximum number of rows (default 50).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            var accounts = topology.ResourceAccounts.ToLookup(account => account.ObjectId, StringComparer.OrdinalIgnoreCase);
            var groups = topology.Groups.ToLookup(group => group.Id, StringComparer.OrdinalIgnoreCase);
            return Page(
                topology.CallQueues.Where(item => Contains(item.Name, search)),
                limit,
                item => new
                {
                    name = item.Name,
                    identity = item.Identity,
                    routingMethod = item.RoutingMethod,
                    agentAlertTime = item.AgentAlertTime,
                    directAgentCount = item.AgentObjectIds.Count,
                    agentGroups = item.DistributionListIds.Select(id => new
                    {
                        id,
                        displayName = groups[id].FirstOrDefault()?.DisplayName
                    }),
                    resourceAccounts = item.ResourceAccountObjectIds.Select(id => DescribeAccount(accounts, id)),
                    hasNoAgents = item.HasNoAgents
                });
        });

    [McpServerTool(Name = "list_resource_accounts", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists Teams resource accounts with their user principal name, phone number, kind (auto attendant or call queue) and whether the account is enabled. Read-only.")]
    public Task<string> ListResourceAccounts(
        [Description("Only display names, UPNs or phone numbers that contain this text.")] string? search = null,
        [Description("Only this kind: autoAttendant, callQueue or unknown.")] string? kind = null,
        [Description("Maximum number of rows (default 50).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            ResourceAccountKind? kindFilter = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                kindFilter = Enum.TryParse<ResourceAccountKind>(kind.Trim(), ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new ArgumentException("kind must be autoAttendant, callQueue or unknown.");
            }

            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return Page(
                topology.ResourceAccounts.Where(item =>
                    (kindFilter is null || item.Kind == kindFilter) &&
                    (Contains(item.DisplayName, search) || Contains(item.UserPrincipalName, search) ||
                     Contains(item.PhoneNumber, search))),
                limit,
                item => (object)item);
        });

    [McpServerTool(Name = "list_m365_groups", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists Microsoft 365 groups of the tenant. A tenant can have hundreds, so give a search text. Read-only.")]
    public Task<string> ListGroups(
        [Description("Only display names or mail nicknames that contain this text.")] string? search = null,
        [Description("Maximum number of rows (default 50).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return Page(
                topology.Groups.Where(item => Contains(item.DisplayName, search) || Contains(item.MailNickname, search)),
                limit,
                item => (object)item);
        });

    [McpServerTool(Name = "tenant_details", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Returns detailed rows from the full tenant snapshot: call flows, menu options, schedules and their date ranges, call queue agents and overflow or timeout actions, phone number assignments and voice users. Call it without a section to see the available sections. The full snapshot takes several queries; it is read once per session unless refresh=true. Read-only.")]
    public Task<string> Details(
        [Description("Section name, for example 'documentation.autoAttendantCallFlows' or 'documentation.schedules'. Omit to list the sections.")] string? section = null,
        [Description("Only rows where any text value contains this text.")] string? search = null,
        [Description("Maximum number of rows (default 50).")] int limit = DefaultLimit,
        [Description("True to read the snapshot from the tenant again.")] bool refresh = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var snapshot = refresh || _tenant.LatestSnapshot is null
                ? await _tenant.CollectSnapshotAsync(cancellationToken)
                : _tenant.LatestSnapshot;
            var sections = Sections(snapshot);
            if (string.IsNullOrWhiteSpace(section))
            {
                return new
                {
                    retrievedAtUtc = snapshot.RetrievedAtUtc,
                    sections = sections.Select(entry => new { name = entry.Key, rows = entry.Value.Count })
                };
            }

            var match = sections.FirstOrDefault(entry => string.Equals(entry.Key, section.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Value is null)
            {
                throw new ArgumentException(
                    $"'{section}' is not a section. Sections: {string.Join(", ", sections.Select(entry => entry.Key))}.");
            }

            return Page(
                match.Value.Where(row => string.IsNullOrWhiteSpace(search) || RowContains(row, search)),
                limit,
                row => (object)row!);
        });

    [McpServerTool(Name = "audit_history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the local PhoneDesk audit log, newest first: every operation the desktop app or this server ran, with time, operator, tenant, target, outcome and error. Reads local files only.")]
    public string AuditHistory(
        [Description("Maximum number of records (default 50).")] int limit = DefaultLimit,
        [Description("Only operations whose name or target contains this text.")] string? search = null,
        [Description("Only this outcome: success, failure or cancelled.")] string? outcome = null,
        [Description("True to show changes only and leave out reads.")] bool changesOnly = false,
        [Description("True to show only the connected tenant.")] bool connectedTenantOnly = false)
        => McpJson.Run(() =>
        {
            var records = _auditLog.Read().Where(record =>
                (string.IsNullOrWhiteSpace(search) || Contains(record.Operation, search) || Contains(record.Target, search)) &&
                (string.IsNullOrWhiteSpace(outcome) ||
                 string.Equals(record.Outcome.ToString(), outcome.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                (!changesOnly || record.Kind != Audit.AuditKind.Read) &&
                (!connectedTenantOnly ||
                 string.Equals(record.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase)));
            return Page(records, limit, record => (object)new
            {
                record.TimestampUtc,
                record.Operation,
                record.Target,
                record.Outcome,
                record.Kind,
                record.Operator,
                record.TenantName,
                record.TenantId,
                errorDetail = McpJson.Truncate(record.ErrorDetail, 500),
                record.AppVersion
            });
        });

    private Dictionary<string, JsonArray> Sections(TopologySnapshotDocument snapshot)
    {
        var root = JsonNode.Parse(_tenantAsCode.SerializeTopology(snapshot))!.AsObject();
        var sections = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root)
        {
            if (property.Value is JsonArray array)
            {
                sections[property.Key] = array;
            }
        }

        if (root["documentation"] is JsonObject documentation)
        {
            foreach (var property in documentation)
            {
                sections["documentation." + property.Key] = property.Value switch
                {
                    JsonArray array => array,
                    JsonNode node => new JsonArray(node.DeepClone()),
                    _ => new JsonArray()
                };
            }
        }

        return sections;
    }

    private static bool RowContains(JsonNode? row, string search)
        => row is not null && row.ToJsonString().Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    private static object DescribeAccount(ILookup<string, TopologyResourceAccount> accounts, string objectId)
    {
        var account = accounts[objectId].FirstOrDefault();
        return new
        {
            objectId,
            userPrincipalName = account?.UserPrincipalName,
            phoneNumber = account?.PhoneNumber
        };
    }

    private static bool Contains(string? value, string? search)
        => string.IsNullOrWhiteSpace(search) ||
           (value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static object Page<T>(IEnumerable<T> source, int limit, Func<T, object> project)
    {
        var all = source.ToList();
        var take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        return new
        {
            total = all.Count,
            returned = Math.Min(take, all.Count),
            truncated = all.Count > take,
            rows = all.Take(take).Select(project)
        };
    }
}
