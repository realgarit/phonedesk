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
        var associations = ParseRows(rawData.ResourceAccounts, "DOCDATA_ASSOC:", 4)
            .Select(parts => new AssociationRow(parts[1], parts[2], parts[3]))
            .ToArray();
        var resourceAccounts = ParseRows(rawData.ResourceAccounts, "DOCDATA_RA:", 5)
            .Select(parts => new ResourceAccountInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4],
                associations
                    .Where(item => string.Equals(item.ResourceAccountId, parts[2], StringComparison.Ordinal))
                    .Select(item => new ResourceAccountAssociationSnapshot(item.ConfigurationId, item.ConfigurationType))
                    .ToArray()))
            .ToArray();
        EnsureParents(
            associations.Select(item => item.ResourceAccountId),
            resourceAccounts.Select(item => item.ObjectId),
            "resource-account association");

        var menuOptions = ParseRows(rawData.AutoAttendants, "DOCDATA_AA_MENU:", 5)
            .Select(parts => new AutoAttendantMenuRow(parts[0], parts[1], parts[2], parts[3], parts[4]))
            .ToArray();
        var callFlows = ParseRows(rawData.AutoAttendants, "DOCDATA_AA_CF:", 3)
            .Select(parts => new AutoAttendantCallFlowRow(parts[0], parts[1], parts[2]))
            .ToArray();
        var scheduleAssociations = ParseRows(rawData.AutoAttendants, "DOCDATA_AA_CHA:", 4)
            .Select(parts => new AutoAttendantScheduleRow(parts[0], parts[1], parts[2], parts[3]))
            .ToArray();
        var operators = ParseRows(rawData.AutoAttendants, "DOCDATA_AA_OP:", 3)
            .Select(parts => new AutoAttendantOperatorRow(parts[0], parts[1], parts[2]))
            .ToArray();
        var autoAttendants = ParseRows(rawData.AutoAttendants, "DOCDATA_AA:", 6)
            .Select(parts => new AutoAttendantInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5],
                menuOptions
                    .Where(item => string.Equals(item.AutoAttendantName, parts[0], StringComparison.Ordinal))
                    .Select(item => new AutoAttendantMenuOptionSnapshot(item.FlowName, item.Key, item.Action, item.TargetId))
                    .ToArray(),
                callFlows
                    .Where(item => string.Equals(item.AutoAttendantName, parts[0], StringComparison.Ordinal))
                    .Select(item => new AutoAttendantCallFlowSnapshot(item.FlowName, item.MenuName))
                    .ToArray(),
                scheduleAssociations
                    .Where(item => string.Equals(item.AutoAttendantName, parts[0], StringComparison.Ordinal))
                    .Select(item => new AutoAttendantScheduleAssociationSnapshot(item.Type, item.ScheduleId, item.CallFlowId))
                    .ToArray(),
                operators
                    .Where(item => string.Equals(item.AutoAttendantName, parts[0], StringComparison.Ordinal))
                    .Select(item => new AutoAttendantOperatorSnapshot(item.Type, item.TargetId))
                    .SingleOrDefault()))
            .ToArray();
        EnsureUniqueParentNames(autoAttendants.Select(item => item.Name), "auto attendant");
        var autoAttendantNames = autoAttendants.Select(item => item.Name).ToArray();
        EnsureParents(menuOptions.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant menu option");
        EnsureParents(callFlows.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant call flow");
        EnsureParents(scheduleAssociations.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant schedule association");
        EnsureParents(operators.Select(item => item.AutoAttendantName), autoAttendantNames, "auto-attendant operator");

        var agents = ParseRows(rawData.CallQueues, "DOCDATA_CQ_AGENT:", 3)
            .Select(parts => new CallQueueAgentRow(
                parts[0], parts[1], parts[2],
                parts.Length >= 4 ? parts[3] : parts[1],
                parts.Length >= 5 ? parts[4] : string.Empty))
            .ToArray();
        var distributionLists = ParseRows(rawData.CallQueues, "DOCDATA_CQ_DL:", 2)
            .Select(parts => new CallQueueDistributionListRow(parts[0], parts[1]))
            .ToArray();
        var overflows = ParseRows(rawData.CallQueues, "DOCDATA_CQ_OVERFLOW:", 4)
            .Select(parts => new QueueActionRow(parts[0], parts[1], parts[2], parts[3]))
            .ToArray();
        var timeouts = ParseRows(rawData.CallQueues, "DOCDATA_CQ_TIMEOUT:", 4)
            .Select(parts => new QueueActionRow(parts[0], parts[1], parts[2], parts[3]))
            .ToArray();
        var callQueues = ParseRows(rawData.CallQueues, "DOCDATA_CQ:", 10)
            .Select(parts => new CallQueueInventorySnapshot(
                parts[0], parts[1], parts[2], parts[3], parts[4], ParseCount(parts[5], "call-queue agent"),
                parts[6], parts[7], parts[8], parts[9],
                agents
                    .Where(item => string.Equals(item.CallQueueName, parts[0], StringComparison.Ordinal))
                    .Select(item => new CallQueueAgentSnapshot(item.ObjectId, item.OptIn, item.DisplayName, item.UserPrincipalName))
                    .ToArray(),
                distributionLists
                    .Where(item => string.Equals(item.CallQueueName, parts[0], StringComparison.Ordinal))
                    .Select(item => item.GroupId)
                    .ToArray(),
                overflows
                    .Where(item => string.Equals(item.CallQueueName, parts[0], StringComparison.Ordinal))
                    .Select(item => new QueueThresholdActionSnapshot(item.Action, item.TargetId, item.Threshold))
                    .SingleOrDefault(),
                timeouts
                    .Where(item => string.Equals(item.CallQueueName, parts[0], StringComparison.Ordinal))
                    .Select(item => new QueueThresholdActionSnapshot(item.Action, item.TargetId, item.Threshold))
                    .SingleOrDefault()))
            .ToArray();
        EnsureUniqueParentNames(callQueues.Select(item => item.Name), "call queue");
        var callQueueNames = callQueues.Select(item => item.Name).ToArray();
        EnsureParents(agents.Select(item => item.CallQueueName), callQueueNames, "call-queue agent");
        EnsureParents(distributionLists.Select(item => item.CallQueueName), callQueueNames, "call-queue distribution list");
        EnsureParents(overflows.Select(item => item.CallQueueName), callQueueNames, "call-queue overflow action");
        EnsureParents(timeouts.Select(item => item.CallQueueName), callQueueNames, "call-queue timeout action");

        var dateRanges = ParseRows(rawData.Schedules, "DOCDATA_SCHED_DR:", 3)
            .Select(parts => new ScheduleDateRangeRow(parts[0], parts[1], parts[2]))
            .ToArray();
        var weeklyRanges = ParseRows(rawData.Schedules, "DOCDATA_SCHED_WK:", 4)
            .Select(parts => new ScheduleWeeklyRangeRow(parts[0], parts[1], parts[2], parts[3]))
            .ToArray();
        var schedules = ParseRows(rawData.Schedules, "DOCDATA_SCHED:", 4)
            .Select(parts => new ScheduleInventorySnapshot(
                parts[0], parts[1], parts[2], ParseCount(parts[3], "schedule date-range"),
                dateRanges
                    .Where(item => string.Equals(item.ScheduleName, parts[0], StringComparison.Ordinal))
                    .Select(item => new ScheduleDateRangeSnapshot(item.Start, item.End))
                    .ToArray(),
                weeklyRanges
                    .Where(item => string.Equals(item.ScheduleName, parts[0], StringComparison.Ordinal))
                    .Select(item => new ScheduleWeeklyRangeSnapshot(item.Day, item.Start, item.End))
                    .ToArray()))
            .ToArray();
        EnsureUniqueParentNames(schedules.Select(item => item.Name), "schedule");
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
            callQueues,
            schedules,
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

    private static void EnsureUniqueParentNames(IEnumerable<string> names, string description)
    {
        var values = names.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidDataException(
                $"The frozen report format requires unique, non-empty {description} names for a complete snapshot.");
        }
    }

    private sealed record AssociationRow(string ResourceAccountId, string ConfigurationId, string ConfigurationType);
    private sealed record AutoAttendantMenuRow(string AutoAttendantName, string FlowName, string Key, string Action, string TargetId);
    private sealed record AutoAttendantCallFlowRow(string AutoAttendantName, string FlowName, string MenuName);
    private sealed record AutoAttendantScheduleRow(string AutoAttendantName, string Type, string ScheduleId, string CallFlowId);
    private sealed record AutoAttendantOperatorRow(string AutoAttendantName, string Type, string TargetId);
    private sealed record CallQueueAgentRow(string CallQueueName, string ObjectId, string OptIn, string DisplayName, string UserPrincipalName);
    private sealed record CallQueueDistributionListRow(string CallQueueName, string GroupId);
    private sealed record QueueActionRow(string CallQueueName, string Action, string TargetId, string Threshold);
    private sealed record ScheduleDateRangeRow(string ScheduleName, string Start, string End);
    private sealed record ScheduleWeeklyRangeRow(string ScheduleName, string Day, string Start, string End);
}
