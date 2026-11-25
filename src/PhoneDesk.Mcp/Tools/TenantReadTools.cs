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
        [Description("Only auto attendants whose name, identity, language or time zone contains this text.")] string? search = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        [Description("Number of rows to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            var accounts = topology.ResourceAccounts.ToLookup(account => account.ObjectId, StringComparer.OrdinalIgnoreCase);
            return Page(
                topology.AutoAttendants.Where(item =>
                    Contains(item.Name, search) || Contains(item.Identity, search) ||
                    Contains(item.LanguageId, search) || Contains(item.TimeZoneId, search)),
                limit,
                offset,
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
        [Description("Only call queues whose name, identity or routing method contains this text.")] string? search = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        [Description("Number of rows to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            var accounts = topology.ResourceAccounts.ToLookup(account => account.ObjectId, StringComparer.OrdinalIgnoreCase);
            var groups = topology.Groups.ToLookup(group => group.Id, StringComparer.OrdinalIgnoreCase);
            return Page(
                topology.CallQueues.Where(item =>
                    Contains(item.Name, search) || Contains(item.Identity, search) || Contains(item.RoutingMethod, search)),
                limit,
                offset,
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
        [Description("Only accounts whose display name, UPN, phone number or object ID contains this text.")] string? search = null,
        [Description("Only this kind: autoAttendant, callQueue or unknown.")] string? kind = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        [Description("Number of rows to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            ResourceAccountKind? kindFilter = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                kindFilter = ParseName<ResourceAccountKind>(kind, "kind");
            }

            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return Page(
                topology.ResourceAccounts.Where(item =>
                    (kindFilter is null || item.Kind == kindFilter) &&
                    (Contains(item.DisplayName, search) || Contains(item.UserPrincipalName, search) ||
                     Contains(item.PhoneNumber, search) || Contains(item.ObjectId, search))),
                limit,
                offset,
                item => (object)item);
        });

    [McpServerTool(Name = "list_m365_groups", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists Microsoft 365 groups of the tenant. A tenant can have hundreds, so give a search text. Read-only.")]
    public Task<string> ListGroups(
        [Description("Only groups whose display name, mail nickname, description or object ID contains this text.")] string? search = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("True to query the tenant again.")] bool refresh = false,
        [Description("Number of rows to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            return Page(
                topology.Groups.Where(item =>
                    Contains(item.DisplayName, search) || Contains(item.MailNickname, search) ||
                    Contains(item.Description, search) || Contains(item.Id, search)),
                limit,
                offset,
                item => (object)item);
        });

    [McpServerTool(Name = "tenant_details", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Returns detailed rows from the full tenant snapshot: call flows, menu options, schedules and their date ranges, call queue agents and overflow or timeout actions, phone number assignments and voice users. Call it without a section to see the available sections. The full snapshot takes several queries, so it is kept for the session; it is read again after a change this server made, or with refresh=true. 'retrievedAtUtc' says how old the rows are. Read-only.")]
    public Task<string> Details(
        [Description("Section name, for example 'documentation.autoAttendantCallFlows' or 'documentation.schedules'. Omit to list the sections.")] string? section = null,
        [Description("Only rows where a value (name, number, ID, text) contains this text. Property names are not searched.")] string? search = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("True to read the snapshot from the tenant again.")] bool refresh = false,
        [Description("Number of rows to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            _tenant.RequireReady();

            // A snapshot from before a change of this session would not show the change.
            var snapshot = refresh || _tenant.CurrentSnapshot is null
                ? await _tenant.CollectSnapshotAsync(cancellationToken)
                : _tenant.CurrentSnapshot;
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
                offset,
                row => (object)row!,
                snapshot.RetrievedAtUtc);
        });

    [McpServerTool(Name = "audit_history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the local PhoneDesk audit log, newest first: every operation the desktop app or this server ran, with time, operator, tenant, target, outcome and error. Reads local files only.")]
    public string AuditHistory(
        [Description("Maximum number of records (default 50, at most 500).")] int limit = DefaultLimit,
        [Description("Only operations whose name or target contains this text.")] string? search = null,
        [Description("Only this outcome: success, failure or cancelled.")] string? outcome = null,
        [Description("True to show changes to the tenant only and leave out reads, module checks, sign-ins and sign-outs.")] bool changesOnly = false,
        [Description("True to show only the connected tenant.")] bool connectedTenantOnly = false,
        [Description("Only records from this day on, as yyyy-MM-dd (UTC).")] string? from = null,
        [Description("Only records up to and including this day, as yyyy-MM-dd (UTC).")] string? to = null,
        [Description("Number of records to skip, to read the next part of a long list (see nextOffset in the result).")] int offset = 0)
        => McpJson.Run(() =>
        {
            // A misspelled outcome must not read as "no such records".
            var outcomeFilter = string.IsNullOrWhiteSpace(outcome)
                ? (Audit.AuditOutcome?)null
                : ParseName<Audit.AuditOutcome>(outcome, "outcome");
            var fromDay = ParseDay(from, "from");
            var toDay = ParseDay(to, "to");
            if (fromDay > toDay)
            {
                throw new ArgumentException("'from' is after 'to'.");
            }

            var records = _auditLog.Read().Where(record =>
                (string.IsNullOrWhiteSpace(search) || Contains(record.Operation, search) || Contains(record.Target, search)) &&
                (outcomeFilter is null || record.Outcome == outcomeFilter) &&
                (fromDay is null || DateOnly.FromDateTime(record.TimestampUtc.UtcDateTime) >= fromDay) &&
                (toDay is null || DateOnly.FromDateTime(record.TimestampUtc.UtcDateTime) <= toDay) &&
                (!changesOnly || (record.Kind != Audit.AuditKind.Read && !IsSessionOperation(record.Operation))) &&
                (!connectedTenantOnly ||
                 string.Equals(record.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase)));
            return Page(records, limit, offset, record => (object)new
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

    /// <summary>
    /// True when a value of the row contains the text. The values are compared as they are, not
    /// as JSON text: JSON writes "+" and umlauts as escapes, so a phone number or "Zürich" would
    /// never match there.
    /// </summary>
    internal static bool RowContains(JsonNode? row, string search)
    {
        var wanted = search.Trim();
        return row switch
        {
            null => false,
            JsonObject item => item.Any(property => RowContains(property.Value, wanted)),
            JsonArray array => array.Any(element => RowContains(element, wanted)),
            JsonValue value => (value.TryGetValue<string>(out var text) ? text : value.ToJsonString())
                .Contains(wanted, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Reads an enum value by its name. A number or an unknown name is an error that lists the names.</summary>
    private static T ParseName<T>(string value, string parameter) where T : struct, Enum
        => !int.TryParse(value.Trim(), out _) && Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException(
                $"'{value}' is not a valid {parameter}. Valid values: " +
                string.Join(", ", Enum.GetNames<T>().Select(name => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name))) + ".");

    private static DateOnly? ParseDay(string? value, string parameter)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day)
                ? day
                : throw new ArgumentException($"'{value}' is not a date for '{parameter}'. Use yyyy-MM-dd, for example 2026-03-01.");

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

    /// <summary>Module checks, sign-ins and sign-outs: logged as operations, but they change nothing in the tenant.</summary>
    private static bool IsSessionOperation(string? operation)
        => operation is not null &&
           (operation.Equals("CheckModules", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("Connect", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("Disconnect", StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string? search)
        => string.IsNullOrWhiteSpace(search) ||
           (value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static object Page<T>(IEnumerable<T> source, int limit, int offset, Func<T, object> project, DateTimeOffset? retrievedAtUtc = null)
    {
        var all = source.ToList();
        var take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var skip = Math.Max(0, offset);
        var rows = all.Skip(skip).Take(take).ToList();
        var more = all.Count > skip + rows.Count;
        return new
        {
            retrievedAtUtc,
            total = all.Count,
            offset = skip,
            returned = rows.Count,
            truncated = more,
            nextOffset = more ? skip + rows.Count : (int?)null,
            rows = rows.Select(project)
        };
    }
}
