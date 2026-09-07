using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class TenantDocumentationSnapshotParser : ITenantDocumentationSnapshotParser
{
    public TenantDocumentationSnapshot Parse(TenantDocumentationRawData rawData)
    {
        ArgumentNullException.ThrowIfNull(rawData);

        RequireMarkers(rawData.ResourceAccounts,
            "DOCDATA_RA_START", "DOCDATA_RA_END", "DOCDATA_ASSOC_START", "DOCDATA_ASSOC_END");
        RequireMarkers(rawData.AutoAttendants, "DOCDATA_AA_START", "DOCDATA_AA_END");
        RequireMarkers(rawData.CallQueues, "DOCDATA_CQ_START", "DOCDATA_CQ_END");
        RequireMarkers(rawData.Schedules, "DOCDATA_SCHED_START", "DOCDATA_SCHED_END");
        RequireMarkers(rawData.PhoneNumbers, "DOCDATA_PHONE_START", "DOCDATA_PHONE_END");
        RequireMarkers(rawData.VoiceUsers, "DOCDATA_USER_START", "DOCDATA_USER_END");

        var tenant = ParseTenant(rawData.Tenant);
        var associations = ParseWithOccurrences(
            rawData.ResourceAccounts, "DOCDATA_ASSOC:", 4,
            (parts, occurrence) => new AssociationRow(parts[1], parts[2], parts[3], occurrence));
        var resourceAccounts = ParseRows(rawData.ResourceAccounts, "DOCDATA_RA:", 5)
            .Select(parts => new ResourceAccountInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4],
                associations
                    .Where(item => string.Equals(item.ResourceAccountId, parts[2], StringComparison.Ordinal))
                    .Select(item => new ResourceAccountAssociationSnapshot(
                        item.ConfigurationId, item.ConfigurationType, item.Occurrence))
                    .ToArray()))
            .ToArray();
        EnsureParents(
            associations.Select(item => item.ResourceAccountId),
            resourceAccounts.Select(item => item.ObjectId),
            "resource-account association");

        var autoAttendants = ParseRows(rawData.AutoAttendants, "DOCDATA_AA:", 6)
            .Select(parts => new AutoAttendantInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]))
            .ToArray();
        string? ResolveAutoAttendant(string name) => ResolveParentIdentity(
            name, autoAttendants, item => item.Name, item => item.Identity);
        var menuOptions = ParseWithOccurrences(
            rawData.AutoAttendants, "DOCDATA_AA_MENU:", 5,
            (parts, occurrence) => new AutoAttendantMenuOptionSnapshot(
                parts[0], ResolveAutoAttendant(parts[0]), parts[1], parts[2], parts[3], parts[4], occurrence));
        var callFlows = ParseWithOccurrences(
            rawData.AutoAttendants, "DOCDATA_AA_CF:", 3,
            (parts, occurrence) => new AutoAttendantCallFlowSnapshot(
                parts[0], ResolveAutoAttendant(parts[0]), parts[1], parts[2], occurrence));
        var scheduleAssociations = ParseWithOccurrences(
            rawData.AutoAttendants, "DOCDATA_AA_CHA:", 4,
            (parts, occurrence) => new AutoAttendantScheduleAssociationSnapshot(
                parts[0], ResolveAutoAttendant(parts[0]), parts[1], parts[2], parts[3], occurrence));
        var operators = ParseWithOccurrences(
            rawData.AutoAttendants, "DOCDATA_AA_OP:", 3,
            (parts, occurrence) => new AutoAttendantOperatorSnapshot(
                parts[0], ResolveAutoAttendant(parts[0]), parts[1], parts[2], occurrence));
        var autoAttendantNames = autoAttendants.Select(item => item.Name).ToArray();
        EnsureParents(menuOptions.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant menu option");
        EnsureParents(callFlows.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant call flow");
        EnsureParents(scheduleAssociations.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant schedule association");
        EnsureParents(operators.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant operator");

        var callQueues = ParseRows(rawData.CallQueues, "DOCDATA_CQ:", 10)
            .Select(parts => new CallQueueInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], ParseCount(parts[5], "call-queue agent"),
                parts[6], parts[7], parts[8], parts[9]))
            .ToArray();
        string? ResolveCallQueue(string name) => ResolveParentIdentity(
            name, callQueues, item => item.Name, item => item.Identity);
        var agents = ParseWithOccurrences(
            rawData.CallQueues, "DOCDATA_CQ_AGENT:", 3,
            (parts, occurrence) => new CallQueueAgentSnapshot(
                parts[0], ResolveCallQueue(parts[0]), parts[1], parts[2],
                parts.Length >= 4 ? parts[3] : parts[1],
                parts.Length >= 5 ? parts[4] : string.Empty,
                occurrence));
        var distributionLists = ParseWithOccurrences(
            rawData.CallQueues, "DOCDATA_CQ_DL:", 2,
            (parts, occurrence) => new CallQueueDistributionListSnapshot(
                parts[0], ResolveCallQueue(parts[0]), parts[1], occurrence));
        var overflows = ParseWithOccurrences(
            rawData.CallQueues, "DOCDATA_CQ_OVERFLOW:", 4,
            (parts, occurrence) => new QueueThresholdActionSnapshot(
                parts[0], ResolveCallQueue(parts[0]), "overflow", parts[1], parts[2], parts[3], occurrence));
        var timeouts = ParseWithOccurrences(
            rawData.CallQueues, "DOCDATA_CQ_TIMEOUT:", 4,
            (parts, occurrence) => new QueueThresholdActionSnapshot(
                parts[0], ResolveCallQueue(parts[0]), "timeout", parts[1], parts[2], parts[3], occurrence));
        var callQueueNames = callQueues.Select(item => item.Name).ToArray();
        EnsureParents(agents.Select(item => item.CallQueueName), callQueueNames, "call-queue agent");
        EnsureParents(distributionLists.Select(item => item.CallQueueName), callQueueNames, "call-queue distribution list");
        EnsureParents(overflows.Select(item => item.CallQueueName), callQueueNames, "call-queue overflow action");
        EnsureParents(timeouts.Select(item => item.CallQueueName), callQueueNames, "call-queue timeout action");

        var schedules = ParseRows(rawData.Schedules, "DOCDATA_SCHED:", 4)
            .Select(parts => new ScheduleInventorySnapshot(
                parts[0], parts[1], parts[2], ParseCount(parts[3], "schedule date-range")))
            .ToArray();
        string? ResolveSchedule(string name) => ResolveParentIdentity(
            name, schedules, item => item.Name, item => item.Id);
        var dateRanges = ParseWithOccurrences(
            rawData.Schedules, "DOCDATA_SCHED_DR:", 3,
            (parts, occurrence) => new ScheduleDateRangeSnapshot(
                parts[0], ResolveSchedule(parts[0]), parts[1], parts[2], occurrence));
        var weeklyRanges = ParseWithOccurrences(
            rawData.Schedules, "DOCDATA_SCHED_WK:", 4,
            (parts, occurrence) => new ScheduleWeeklyRangeSnapshot(
                parts[0], ResolveSchedule(parts[0]), parts[1], parts[2], parts[3], occurrence));
        var scheduleNames = schedules.Select(item => item.Name).ToArray();
        EnsureParents(dateRanges.Select(item => item.ScheduleName), scheduleNames, "schedule date range");
        EnsureParents(weeklyRanges.Select(item => item.ScheduleName), scheduleNames, "schedule weekly range");

        var phoneNumbers = ParseRows(rawData.PhoneNumbers, "DOCDATA_PHONE:", 7)
            .Select(parts => new PhoneNumberInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6]))
            .ToArray();
        var voiceUsers = ParseRows(rawData.VoiceUsers, "DOCDATA_USER:", 6)
            .Select(parts => new VoiceUserInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]))
            .ToArray();

        return new TenantDocumentationSnapshot(
            tenant,
            resourceAccounts,
            autoAttendants,
            menuOptions,
            callFlows,
            scheduleAssociations,
            operators,
            callQueues,
            agents,
            distributionLists,
            overflows.Concat(timeouts).ToArray(),
            schedules,
            dateRanges,
            weeklyRanges,
            phoneNumbers,
            voiceUsers);
    }

    private static TenantInformationSnapshot ParseTenant(string raw)
    {
        var rows = ParseRows(raw, "DOCDATA_TENANT:", 4);
        if (rows.Count != 1)
        {
            throw new InvalidDataException("Tenant snapshot data must contain exactly one tenant record.");
        }
        var parts = rows[0];
        return new TenantInformationSnapshot(parts[0], parts[1], parts[2], parts[3]);
    }

    private static IReadOnlyList<T> ParseWithOccurrences<T>(
        string raw,
        string prefix,
        int minimumFields,
        Func<string[], int, T> create)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<T>();
        foreach (var parts in ParseRows(raw, prefix, minimumFields)
                     .OrderBy(row => string.Join('\u001f', row), StringComparer.Ordinal))
        {
            var baseIdentity = JoinKey(parts);
            occurrences.TryGetValue(baseIdentity, out var occurrence);
            results.Add(create(parts, occurrence));
            occurrences[baseIdentity] = occurrence + 1;
        }
        return results;
    }

    private static IReadOnlyList<string[]> ParseRows(string raw, string prefix, int minimumFields)
    {
        var rows = new List<string[]>();
        foreach (var line in Lines(raw))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var parts = line[prefix.Length..].Split('|').Select(value => value.Trim()).ToArray();
            if (parts.Length < minimumFields)
            {
                throw new InvalidDataException($"Malformed {prefix.TrimEnd(':')} snapshot row.");
            }
            rows.Add(parts);
        }
        return rows;
    }

    private static IEnumerable<string> Lines(string raw)
        => (raw ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim());

    private static int ParseCount(string value, string description)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : throw new InvalidDataException($"Invalid {description} count '{value}'.");

    private static void RequireMarkers(string raw, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (!(raw ?? string.Empty).Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Tenant snapshot data is missing the {marker} marker.");
            }
        }
    }

    private static void EnsureParents(
        IEnumerable<string> childParentIds,
        IEnumerable<string> parentIds,
        string description)
    {
        var parents = parentIds.ToHashSet(StringComparer.Ordinal);
        var missing = childParentIds.FirstOrDefault(id => !parents.Contains(id));
        if (missing is not null)
        {
            throw new InvalidDataException($"The {description} references unknown parent '{missing}'.");
        }
    }

    private static string? ResolveParentIdentity<T>(
        string parentName,
        IEnumerable<T> parents,
        Func<T, string> getName,
        Func<T, string> getIdentity)
    {
        var identities = parents
            .Where(parent => string.Equals(getName(parent), parentName, StringComparison.Ordinal))
            .Select(getIdentity)
            .Take(2)
            .ToArray();
        return identities.Length == 1 ? identities[0] : null;
    }

    private static string JoinKey(params string[] parts) => string.Join('\u001f', parts);

    private sealed record AssociationRow(
        string ResourceAccountId,
        string ConfigurationId,
        string ConfigurationType,
        int Occurrence);
}
