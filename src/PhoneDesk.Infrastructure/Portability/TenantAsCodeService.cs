using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Services;

public sealed class TenantAsCodeService : ITenantAsCodeService
{
    private static readonly string[] DayOrder =
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    };

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions(writeIndented: true);
    private static readonly JsonSerializerOptions ComparisonOptions = CreateOptions(writeIndented: false);

    public string SerializeConfiguration(ConfigurationDocument document)
    {
        ValidateConfiguration(document);
        var normalized = NormalizeConfiguration(document);
        return NormalizeNewlines(JsonSerializer.Serialize(normalized, JsonOptions));
    }

    public ConfigurationDocument DeserializeConfiguration(string json)
    {
        var document = DeserializeStrict<ConfigurationDocument>(json, "configuration");
        ValidateConfiguration(document);
        document = NormalizeConfiguration(document);
        return document;
    }

    public IReadOnlyList<ConfigurationChange> CompareConfigurations(
        ConfigurationDocument current,
        ConfigurationDocument imported)
    {
        ValidateConfiguration(current);
        ValidateConfiguration(imported);
        current = NormalizeConfiguration(current);
        imported = NormalizeConfiguration(imported);

        var currentNode = JsonSerializer.SerializeToNode(current.Configuration, ComparisonOptions);
        var importedNode = JsonSerializer.SerializeToNode(imported.Configuration, ComparisonOptions);
        var changes = new List<ConfigurationChange>();
        CompareJsonNodes("configuration", currentNode, importedNode, changes);
        return changes;
    }

    public string SerializeTopology(TenantTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return SerializeTopology(CreateTopologyDocument(
            topology,
            documentation: null,
            PortabilitySchema.TopologyLegacyVersion));
    }

    public TopologySnapshotDocument CreateTopologySnapshot(
        TenantTopology topology,
        TenantDocumentationSnapshot documentation)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(documentation);
        return CreateTopologyDocument(
            topology,
            documentation,
            PortabilitySchema.TopologyCurrentVersion);
    }

    public string SerializeTopology(TopologySnapshotDocument document)
    {
        ValidateTopology(document);
        var normalized = NormalizeTopology(document);
        return NormalizeNewlines(JsonSerializer.Serialize(normalized, JsonOptions));
    }

    public TopologySnapshotDocument DeserializeTopology(string json)
    {
        var document = DeserializeStrict<TopologySnapshotDocument>(json, "topology snapshot");
        ValidateTopology(document);
        return NormalizeTopology(document);
    }

    public IReadOnlyList<TopologyDriftEntry> CompareTopology(
        TopologySnapshotDocument snapshot,
        TenantTopology liveTopology)
    {
        ArgumentNullException.ThrowIfNull(liveTopology);
        var live = CreateTopologyDocument(
            liveTopology,
            documentation: null,
            PortabilitySchema.TopologyLegacyVersion);
        return CompareTopology(snapshot, live);
    }

    public IReadOnlyList<TopologyDriftEntry> CompareTopology(
        TopologySnapshotDocument snapshot,
        TopologySnapshotDocument liveSnapshot)
    {
        ValidateTopology(snapshot);
        ValidateTopology(liveSnapshot);
        snapshot = NormalizeTopology(snapshot);
        var live = NormalizeTopology(liveSnapshot);
        var results = new List<TopologyDriftEntry>();

        CompareObjects(
            "autoAttendant",
            snapshot.AutoAttendants,
            live.AutoAttendants,
            item => item.Identity,
            item => item.Name,
            AutoAttendantProperties,
            results);
        CompareObjects(
            "callQueue",
            snapshot.CallQueues,
            live.CallQueues,
            item => item.Identity,
            item => item.Name,
            CallQueueProperties,
            results);
        CompareObjects(
            "resourceAccount",
            snapshot.ResourceAccounts,
            live.ResourceAccounts,
            item => item.ObjectId,
            item => item.DisplayName,
            ResourceAccountProperties,
            results);
        CompareObjects(
            "group",
            snapshot.Groups,
            live.Groups,
            item => item.Id,
            item => item.DisplayName,
            GroupProperties,
            results);

        if (snapshot.Documentation is not null && live.Documentation is not null)
        {
            CompareDocumentation(snapshot.Documentation, live.Documentation, results);
        }

        return results;
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static T DeserializeStrict<T>(string json, string description)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"The {description} file is empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException($"The {description} document is missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {description} JSON is invalid: {ex.Message}", ex);
        }
    }

    private static ConfigurationDocument NormalizeConfiguration(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Configuration is null)
        {
            return document;
        }

        var businessHours = document.Configuration.BusinessHoursTemplate;
        var holiday = document.Configuration.HolidayTemplate;
        if (businessHours is null || holiday is null)
        {
            return document;
        }

        var normalizedBusinessHours = businessHours with
        {
            WeeklySchedule = (businessHours.WeeklySchedule ?? Array.Empty<PortableDaySchedule>())
                .OrderBy(day => GetDayOrder(day.DayName))
                .ThenBy(day => day.DayName, StringComparer.Ordinal)
                .ToArray()
        };
        var normalizedHoliday = holiday with
        {
            Series = (holiday.Series ?? Array.Empty<PortableHolidayEntry>())
                .OrderBy(entry => entry.Date)
                .ThenBy(entry => entry.Time)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray()
        };
        return document with
        {
            Configuration = document.Configuration with
            {
                BusinessHoursTemplate = normalizedBusinessHours,
                HolidayTemplate = normalizedHoliday
            }
        };
    }

    private static void ValidateConfiguration(ConfigurationDocument document)
    {
        if (document.SchemaVersion != PortabilitySchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported configuration schema version {document.SchemaVersion}; expected {PortabilitySchema.CurrentVersion}.");
        }
        if (!string.Equals(document.Kind, PortabilitySchema.ConfigurationKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Document kind must be '{PortabilitySchema.ConfigurationKind}'.");
        }
        if (document.Configuration is null ||
            document.Configuration.General is null ||
            document.Configuration.AutoAttendantTemplate is null ||
            document.Configuration.CallQueueTemplate is null ||
            document.Configuration.BusinessHoursTemplate is null ||
            document.Configuration.HolidayTemplate is null)
        {
            throw new InvalidDataException("The configuration document is missing a required section.");
        }

        var callQueue = document.Configuration.CallQueueTemplate;
        if (callQueue.OverflowThreshold is < 0 || callQueue.TimeoutThreshold is < 0)
        {
            throw new InvalidDataException("Call queue thresholds cannot be negative.");
        }

        var days = document.Configuration.BusinessHoursTemplate.WeeklySchedule
            ?? throw new InvalidDataException("The weekly schedule is missing.");
        if (days.Count != DayOrder.Length)
        {
            throw new InvalidDataException("The weekly schedule must contain all seven days, including disabled days.");
        }
        if (days.Any(day => day is null || !DayOrder.Contains(day.DayName, StringComparer.Ordinal)) ||
            days.Select(day => day.DayName).Distinct(StringComparer.Ordinal).Count() != days.Count)
        {
            throw new InvalidDataException("The weekly schedule contains an invalid or duplicate day.");
        }

        var holidays = document.Configuration.HolidayTemplate.Series
            ?? throw new InvalidDataException("The holiday series is missing.");
        foreach (var entry in holidays)
        {
            if (entry is null)
            {
                throw new InvalidDataException("The holiday series contains an empty entry.");
            }
            if (entry.EndDate is { } endDate &&
                endDate.ToDateTime(entry.EndTime ?? TimeOnly.MinValue) <= entry.Date.ToDateTime(entry.Time))
            {
                throw new InvalidDataException("A holiday end must be later than its start.");
            }
        }
    }

    private static int GetDayOrder(string dayName)
    {
        var index = Array.IndexOf(DayOrder, dayName);
        return index < 0 ? int.MaxValue : index;
    }

    private static void CompareJsonNodes(
        string path,
        JsonNode? current,
        JsonNode? imported,
        ICollection<ConfigurationChange> changes)
    {
        if (JsonNode.DeepEquals(current, imported))
        {
            return;
        }

        if (current is JsonObject currentObject && imported is JsonObject importedObject)
        {
            foreach (var property in currentObject)
            {
                importedObject.TryGetPropertyValue(property.Key, out var importedValue);
                CompareJsonNodes($"{path}.{property.Key}", property.Value, importedValue, changes);
            }
            return;
        }

        changes.Add(new ConfigurationChange(path, FormatNode(current), FormatNode(imported)));
    }

    private static string FormatNode(JsonNode? node)
    {
        if (node is null)
        {
            return "(not set)";
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        return node.ToJsonString(ComparisonOptions);
    }

    private static TopologySnapshotDocument CreateTopologyDocument(
        TenantTopology topology,
        TenantDocumentationSnapshot? documentation,
        int schemaVersion)
    {
        if (topology.AutoAttendants is null || topology.CallQueues is null ||
            topology.ResourceAccounts is null || topology.Groups is null)
        {
            throw new InvalidDataException("The live topology is missing a required collection.");
        }

        var document = new TopologySnapshotDocument(
            schemaVersion,
            PortabilitySchema.TopologyKind,
            topology.RetrievedAtUtc,
            topology.AutoAttendants.Select(item => new TopologyAutoAttendantSnapshot(
                item.Name,
                item.Identity,
                item.LanguageId,
                item.TimeZoneId,
                item.ResourceAccountObjectIds,
                item.HolidayScheduleIds,
                item.CallTargetObjectIds)).ToArray(),
            topology.CallQueues.Select(item => new TopologyCallQueueSnapshot(
                item.Name,
                item.Identity,
                item.RoutingMethod,
                item.AgentAlertTime,
                item.AgentObjectIds,
                item.DistributionListIds,
                item.ResourceAccountObjectIds)).ToArray(),
            topology.ResourceAccounts.Select(item => new TopologyResourceAccountSnapshot(
                item.DisplayName,
                item.UserPrincipalName,
                item.ObjectId,
                item.PhoneNumber,
                item.Kind,
                item.AccountEnabled)).ToArray(),
            topology.Groups.Select(item => new TopologyGroupSnapshot(
                item.DisplayName,
                item.Id,
                item.MailNickname,
                item.Description)).ToArray(),
            documentation);
        ValidateTopology(document);
        return NormalizeTopology(document);
    }

    private static TopologySnapshotDocument NormalizeTopology(TopologySnapshotDocument document)
        => document with
        {
            AutoAttendants = document.AutoAttendants
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => item with
                {
                    ResourceAccountObjectIds = Sort(item.ResourceAccountObjectIds),
                    HolidayScheduleIds = Sort(item.HolidayScheduleIds),
                    CallTargetObjectIds = Sort(item.CallTargetObjectIds)
                })
                .ToArray(),
            CallQueues = document.CallQueues
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => item with
                {
                    AgentObjectIds = Sort(item.AgentObjectIds),
                    DistributionListIds = Sort(item.DistributionListIds),
                    ResourceAccountObjectIds = Sort(item.ResourceAccountObjectIds)
                })
                .ToArray(),
            ResourceAccounts = document.ResourceAccounts
                .OrderBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray(),
            Groups = document.Groups
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            Documentation = NormalizeDocumentation(document.Documentation)
        };

    private static TenantDocumentationSnapshot? NormalizeDocumentation(TenantDocumentationSnapshot? documentation)
    {
        if (documentation is null)
        {
            return null;
        }

        return documentation with
        {
            ResourceAccounts = documentation.ResourceAccounts
                .OrderBy(item => item.ObjectId, StringComparer.Ordinal)
                .Select(item => item with
                {
                    Associations = SortBy(
                        item.Associations,
                        association => $"{association.ConfigurationType}\u001f{association.ConfigurationId}")
                })
                .ToArray(),
            AutoAttendants = documentation.AutoAttendants
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => item with
                {
                    MenuOptions = item.MenuOptions
                        .OrderBy(option => option.FlowName, StringComparer.Ordinal)
                        .ThenBy(option => option.Key, StringComparer.Ordinal)
                        .ThenBy(option => option.Action, StringComparer.Ordinal)
                        .ThenBy(option => option.TargetId, StringComparer.Ordinal)
                        .ToArray(),
                    CallFlows = SortBy(item.CallFlows, flow => flow.FlowName),
                    ScheduleAssociations = item.ScheduleAssociations
                        .OrderBy(association => association.Type, StringComparer.Ordinal)
                        .ThenBy(association => association.ScheduleId, StringComparer.Ordinal)
                        .ThenBy(association => association.CallFlowId, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray(),
            CallQueues = documentation.CallQueues
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => item with
                {
                    Agents = SortBy(item.Agents, agent => agent.ObjectId),
                    DistributionListIds = Sort(item.DistributionListIds)
                })
                .ToArray(),
            Schedules = documentation.Schedules
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item with
                {
                    DateRanges = item.DateRanges
                        .OrderBy(range => range.Start, StringComparer.Ordinal)
                        .ThenBy(range => range.End, StringComparer.Ordinal)
                        .ToArray(),
                    WeeklyRanges = item.WeeklyRanges
                        .OrderBy(range => GetDayOrder(range.Day))
                        .ThenBy(range => range.Day, StringComparer.Ordinal)
                        .ThenBy(range => range.Start, StringComparer.Ordinal)
                        .ThenBy(range => range.End, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray(),
            PhoneNumbers = SortBy(documentation.PhoneNumbers, number => number.Number),
            VoiceUsers = SortBy(documentation.VoiceUsers, user => user.UserPrincipalName)
        };
    }

    private static IReadOnlyList<T> SortBy<T>(IReadOnlyList<T> values, Func<T, string> getKey)
        => values.OrderBy(getKey, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Sort(IReadOnlyList<string>? values)
        => (values ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static void ValidateTopology(TopologySnapshotDocument document)
    {
        if (document.SchemaVersion is not PortabilitySchema.TopologyLegacyVersion and
            not PortabilitySchema.TopologyCurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported topology schema version {document.SchemaVersion}; expected " +
                $"{PortabilitySchema.TopologyLegacyVersion} or {PortabilitySchema.TopologyCurrentVersion}.");
        }
        if (!string.Equals(document.Kind, PortabilitySchema.TopologyKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Document kind must be '{PortabilitySchema.TopologyKind}'.");
        }
        if (document.AutoAttendants is null || document.CallQueues is null ||
            document.ResourceAccounts is null || document.Groups is null)
        {
            throw new InvalidDataException("The topology document is missing a required collection.");
        }

        ValidateUniqueItems(document.AutoAttendants, item => item.Identity, "auto attendant");
        ValidateUniqueItems(document.CallQueues, item => item.Identity, "call queue");
        ValidateUniqueItems(document.ResourceAccounts, item => item.ObjectId, "resource account");
        ValidateUniqueItems(document.Groups, item => item.Id, "group");

        if (document.AutoAttendants.Any(item =>
                item.ResourceAccountObjectIds is null || item.HolidayScheduleIds is null || item.CallTargetObjectIds is null) ||
            document.CallQueues.Any(item =>
                item.AgentObjectIds is null || item.DistributionListIds is null || item.ResourceAccountObjectIds is null))
        {
            throw new InvalidDataException("A topology object is missing a required identifier collection.");
        }

        if (document.SchemaVersion == PortabilitySchema.TopologyCurrentVersion && document.Documentation is null)
        {
            throw new InvalidDataException("Topology schema version 2 requires tenant documentation data.");
        }
        if (document.Documentation is not null)
        {
            ValidateDocumentation(document.Documentation);
        }
    }

    private static void ValidateDocumentation(TenantDocumentationSnapshot documentation)
    {
        if (documentation.Tenant is null || documentation.ResourceAccounts is null ||
            documentation.AutoAttendants is null || documentation.CallQueues is null ||
            documentation.Schedules is null || documentation.PhoneNumbers is null ||
            documentation.VoiceUsers is null)
        {
            throw new InvalidDataException("The tenant documentation snapshot is missing a required section.");
        }
        if (string.IsNullOrWhiteSpace(documentation.Tenant.Id))
        {
            throw new InvalidDataException("The tenant documentation snapshot requires a tenant identifier.");
        }

        ValidateUniqueItems(documentation.ResourceAccounts, item => item.ObjectId, "documented resource account");
        ValidateUniqueItems(documentation.AutoAttendants, item => item.Identity, "documented auto attendant");
        ValidateUniqueItems(documentation.CallQueues, item => item.Identity, "documented call queue");
        ValidateUniqueItems(documentation.Schedules, item => item.Id, "documented schedule");
        ValidateUniqueItems(documentation.PhoneNumbers, item => item.Number, "documented phone number");
        ValidateUniqueItems(documentation.VoiceUsers, item => item.UserPrincipalName, "documented voice user");

        foreach (var item in documentation.ResourceAccounts)
        {
            ValidateUniqueItems(
                item.Associations ?? throw MissingNested("resource-account associations"),
                association => association.ConfigurationType,
                "resource-account association");
        }
        foreach (var item in documentation.AutoAttendants)
        {
            ValidateUniqueItems(
                item.MenuOptions ?? throw MissingNested("auto-attendant menu options"),
                option => $"{option.FlowName}\u001f{option.Key}",
                "auto-attendant menu option");
            ValidateUniqueItems(
                item.CallFlows ?? throw MissingNested("auto-attendant call flows"),
                flow => flow.FlowName,
                "auto-attendant call flow");
            ValidateUniqueItems(
                item.ScheduleAssociations ?? throw MissingNested("auto-attendant schedule associations"),
                association => $"{association.Type}\u001f{association.ScheduleId}",
                "auto-attendant schedule association");
        }
        foreach (var item in documentation.CallQueues)
        {
            ValidateUniqueItems(
                item.Agents ?? throw MissingNested("call-queue agents"),
                agent => agent.ObjectId,
                "call-queue agent");
            if (item.DistributionListIds is null ||
                item.DistributionListIds.Any(string.IsNullOrWhiteSpace) ||
                item.DistributionListIds.Distinct(StringComparer.Ordinal).Count() != item.DistributionListIds.Count)
            {
                throw new InvalidDataException("Call-queue distribution-list identifiers must be present and unique.");
            }
        }
        foreach (var item in documentation.Schedules)
        {
            ValidateUniqueItems(
                item.DateRanges ?? throw MissingNested("schedule date ranges"),
                range => range.Start,
                "schedule date range");
            ValidateUniqueItems(
                item.WeeklyRanges ?? throw MissingNested("schedule weekly ranges"),
                range => $"{range.Day}\u001f{range.Start}\u001f{range.End}",
                "schedule weekly range");
        }
    }

    private static InvalidDataException MissingNested(string description)
        => new($"The tenant documentation snapshot is missing {description}.");

    private static void ValidateUniqueItems<T>(
        IReadOnlyList<T> items,
        Func<T, string> getId,
        string description)
        where T : class
    {
        if (items.Any(item => item is null))
        {
            throw new InvalidDataException($"The topology contains an empty {description} entry.");
        }

        var ids = items.Select(getId).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Each topology {description} must have a stable identifier.");
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new InvalidDataException($"The topology contains a duplicate {description} identifier.");
        }
    }

    private static void CompareObjects<T>(
        string objectType,
        IReadOnlyList<T> snapshot,
        IReadOnlyList<T> live,
        Func<T, string> getId,
        Func<T, string> getName,
        Func<T, IReadOnlyDictionary<string, string?>> getProperties,
        ICollection<TopologyDriftEntry> results)
    {
        var snapshotById = snapshot.ToDictionary(getId, StringComparer.Ordinal);
        var liveById = live.ToDictionary(getId, StringComparer.Ordinal);
        foreach (var id in snapshotById.Keys.Union(liveById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasSnapshot = snapshotById.TryGetValue(id, out var saved);
            var hasLive = liveById.TryGetValue(id, out var current);
            if (!hasSnapshot)
            {
                results.Add(new TopologyDriftEntry(
                    TopologyDriftKind.Added, objectType, id, getName(current!), null, null, null));
                continue;
            }
            if (!hasLive)
            {
                results.Add(new TopologyDriftEntry(
                    TopologyDriftKind.Removed, objectType, id, getName(saved!), null, null, null));
                continue;
            }

            var savedProperties = getProperties(saved!);
            var liveProperties = getProperties(current!);
            foreach (var property in savedProperties.Keys)
            {
                if (!string.Equals(savedProperties[property], liveProperties[property], StringComparison.Ordinal))
                {
                    results.Add(new TopologyDriftEntry(
                        TopologyDriftKind.Changed,
                        objectType,
                        id,
                        getName(current!),
                        property,
                        savedProperties[property],
                        liveProperties[property]));
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string?> AutoAttendantProperties(TopologyAutoAttendantSnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["languageId"] = item.LanguageId,
            ["timeZoneId"] = item.TimeZoneId,
            ["resourceAccountObjectIds"] = Join(item.ResourceAccountObjectIds),
            ["holidayScheduleIds"] = Join(item.HolidayScheduleIds),
            ["callTargetObjectIds"] = Join(item.CallTargetObjectIds),
        };

    private static IReadOnlyDictionary<string, string?> CallQueueProperties(TopologyCallQueueSnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["routingMethod"] = item.RoutingMethod,
            ["agentAlertTime"] = item.AgentAlertTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["agentObjectIds"] = Join(item.AgentObjectIds),
            ["distributionListIds"] = Join(item.DistributionListIds),
            ["resourceAccountObjectIds"] = Join(item.ResourceAccountObjectIds),
        };

    private static IReadOnlyDictionary<string, string?> ResourceAccountProperties(TopologyResourceAccountSnapshot item)
        => new Dictionary<string, string?>
        {
            ["displayName"] = item.DisplayName,
            ["userPrincipalName"] = item.UserPrincipalName,
            ["phoneNumber"] = item.PhoneNumber,
            ["kind"] = item.Kind.ToString(),
            ["accountEnabled"] = item.AccountEnabled.ToString(),
        };

    private static IReadOnlyDictionary<string, string?> GroupProperties(TopologyGroupSnapshot item)
        => new Dictionary<string, string?>
        {
            ["displayName"] = item.DisplayName,
            ["mailNickname"] = item.MailNickname,
            ["description"] = item.Description,
        };

    private static void CompareDocumentation(
        TenantDocumentationSnapshot snapshot,
        TenantDocumentationSnapshot live,
        ICollection<TopologyDriftEntry> results)
    {
        CompareObjects(
            "tenant",
            new[] { snapshot.Tenant },
            new[] { live.Tenant },
            item => item.Id,
            item => item.Name,
            TenantProperties,
            results);
        CompareObjects(
            "resourceAccountInventory",
            snapshot.ResourceAccounts,
            live.ResourceAccounts,
            item => item.ObjectId,
            item => item.Name,
            ResourceAccountInventoryProperties,
            results);
        CompareObjects(
            "autoAttendantInventory",
            snapshot.AutoAttendants,
            live.AutoAttendants,
            item => item.Identity,
            item => item.Name,
            AutoAttendantInventoryProperties,
            results);
        CompareObjects(
            "callQueueInventory",
            snapshot.CallQueues,
            live.CallQueues,
            item => item.Identity,
            item => item.Name,
            CallQueueInventoryProperties,
            results);
        CompareObjects(
            "schedule",
            snapshot.Schedules,
            live.Schedules,
            item => item.Id,
            item => item.Name,
            ScheduleInventoryProperties,
            results);
        CompareObjects(
            "phoneNumber",
            snapshot.PhoneNumbers,
            live.PhoneNumbers,
            item => item.Number,
            item => item.Number,
            PhoneNumberProperties,
            results);
        CompareObjects(
            "voiceUser",
            snapshot.VoiceUsers,
            live.VoiceUsers,
            item => item.UserPrincipalName,
            item => item.Name,
            VoiceUserProperties,
            results);

        CompareInventoryChildren(
            "resourceAccountAssociation",
            ResourceAccountAssociations(snapshot),
            ResourceAccountAssociations(live),
            results);
        CompareInventoryChildren(
            "autoAttendantMenuOption",
            AutoAttendantMenuOptions(snapshot),
            AutoAttendantMenuOptions(live),
            results);
        CompareInventoryChildren(
            "autoAttendantCallFlow",
            AutoAttendantCallFlows(snapshot),
            AutoAttendantCallFlows(live),
            results);
        CompareInventoryChildren(
            "autoAttendantScheduleAssociation",
            AutoAttendantScheduleAssociations(snapshot),
            AutoAttendantScheduleAssociations(live),
            results);
        CompareInventoryChildren(
            "autoAttendantOperator",
            AutoAttendantOperators(snapshot),
            AutoAttendantOperators(live),
            results);
        CompareInventoryChildren(
            "callQueueAgent",
            CallQueueAgents(snapshot),
            CallQueueAgents(live),
            results);
        CompareInventoryChildren(
            "callQueueDistributionList",
            CallQueueDistributionLists(snapshot),
            CallQueueDistributionLists(live),
            results);
        CompareInventoryChildren(
            "callQueueAction",
            CallQueueActions(snapshot),
            CallQueueActions(live),
            results);
        CompareInventoryChildren(
            "scheduleDateRange",
            ScheduleDateRanges(snapshot),
            ScheduleDateRanges(live),
            results);
        CompareInventoryChildren(
            "scheduleWeeklyRange",
            ScheduleWeeklyRanges(snapshot),
            ScheduleWeeklyRanges(live),
            results);
    }

    private static void CompareInventoryChildren(
        string objectType,
        IReadOnlyList<InventoryComparable> snapshot,
        IReadOnlyList<InventoryComparable> live,
        ICollection<TopologyDriftEntry> results)
        => CompareObjects(
            objectType,
            snapshot,
            live,
            item => item.Id,
            item => item.DisplayName,
            item => item.Properties,
            results);

    private static IReadOnlyList<InventoryComparable> ResourceAccountAssociations(TenantDocumentationSnapshot data)
        => data.ResourceAccounts
            .SelectMany(account => account.Associations.Select(association => Comparable(
                $"{account.ObjectId}\u001f{association.ConfigurationType}",
                $"{account.Name} / {association.ConfigurationType}",
                ("configurationId", association.ConfigurationId))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> AutoAttendantMenuOptions(TenantDocumentationSnapshot data)
        => data.AutoAttendants
            .SelectMany(attendant => attendant.MenuOptions.Select(option => Comparable(
                $"{attendant.Identity}\u001f{option.FlowName}\u001f{option.Key}",
                $"{attendant.Name} / {option.FlowName} / {option.Key}",
                ("action", option.Action),
                ("targetId", option.TargetId))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> AutoAttendantCallFlows(TenantDocumentationSnapshot data)
        => data.AutoAttendants
            .SelectMany(attendant => attendant.CallFlows.Select(flow => Comparable(
                $"{attendant.Identity}\u001f{flow.FlowName}",
                $"{attendant.Name} / {flow.FlowName}",
                ("menuName", flow.MenuName))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> AutoAttendantScheduleAssociations(TenantDocumentationSnapshot data)
        => data.AutoAttendants
            .SelectMany(attendant => attendant.ScheduleAssociations.Select(association => Comparable(
                $"{attendant.Identity}\u001f{association.Type}\u001f{association.ScheduleId}",
                $"{attendant.Name} / {association.Type}",
                ("scheduleId", association.ScheduleId),
                ("callFlowId", association.CallFlowId))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> AutoAttendantOperators(TenantDocumentationSnapshot data)
        => data.AutoAttendants
            .Where(attendant => attendant.Operator is not null)
            .Select(attendant => Comparable(
                attendant.Identity,
                $"{attendant.Name} / operator",
                ("type", attendant.Operator!.Type),
                ("targetId", attendant.Operator.TargetId)))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> CallQueueAgents(TenantDocumentationSnapshot data)
        => data.CallQueues
            .SelectMany(queue => queue.Agents.Select(agent => Comparable(
                $"{queue.Identity}\u001f{agent.ObjectId}",
                $"{queue.Name} / {agent.DisplayName}",
                ("optIn", agent.OptIn),
                ("displayName", agent.DisplayName),
                ("userPrincipalName", agent.UserPrincipalName))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> CallQueueDistributionLists(TenantDocumentationSnapshot data)
        => data.CallQueues
            .SelectMany(queue => queue.DistributionListIds.Select(groupId => Comparable(
                $"{queue.Identity}\u001f{groupId}",
                $"{queue.Name} / {groupId}")))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> CallQueueActions(TenantDocumentationSnapshot data)
        => data.CallQueues
            .SelectMany(queue => new[]
            {
                queue.Overflow is null ? null : Comparable(
                    $"{queue.Identity}\u001foverflow",
                    $"{queue.Name} / overflow",
                    ("action", queue.Overflow.Action),
                    ("targetId", queue.Overflow.TargetId),
                    ("threshold", queue.Overflow.Threshold)),
                queue.Timeout is null ? null : Comparable(
                    $"{queue.Identity}\u001ftimeout",
                    $"{queue.Name} / timeout",
                    ("action", queue.Timeout.Action),
                    ("targetId", queue.Timeout.TargetId),
                    ("threshold", queue.Timeout.Threshold))
            })
            .OfType<InventoryComparable>()
            .ToArray();

    private static IReadOnlyList<InventoryComparable> ScheduleDateRanges(TenantDocumentationSnapshot data)
        => data.Schedules
            .SelectMany(schedule => schedule.DateRanges.Select(range => Comparable(
                $"{schedule.Id}\u001f{range.Start}",
                $"{schedule.Name} / {range.Start}",
                ("start", range.Start),
                ("end", range.End))))
            .ToArray();

    private static IReadOnlyList<InventoryComparable> ScheduleWeeklyRanges(TenantDocumentationSnapshot data)
        => data.Schedules
            .SelectMany(schedule => schedule.WeeklyRanges.Select(range => Comparable(
                $"{schedule.Id}\u001f{range.Day}\u001f{range.Start}",
                $"{schedule.Name} / {range.Day} / {range.Start}",
                ("day", range.Day),
                ("start", range.Start),
                ("end", range.End))))
            .ToArray();

    private static InventoryComparable Comparable(
        string id,
        string displayName,
        params (string Name, string? Value)[] properties)
        => new(
            id,
            displayName,
            properties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal));

    private sealed record InventoryComparable(
        string Id,
        string DisplayName,
        IReadOnlyDictionary<string, string?> Properties);

    private static IReadOnlyDictionary<string, string?> TenantProperties(TenantInformationSnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["country"] = item.Country,
            ["language"] = item.Language,
        };

    private static IReadOnlyDictionary<string, string?> ResourceAccountInventoryProperties(ResourceAccountInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["userPrincipalName"] = item.UserPrincipalName,
            ["applicationId"] = item.ApplicationId,
            ["phoneNumber"] = item.PhoneNumber,
        };

    private static IReadOnlyDictionary<string, string?> AutoAttendantInventoryProperties(AutoAttendantInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["language"] = item.Language,
            ["timeZone"] = item.TimeZone,
            ["voice"] = item.Voice,
            ["defaultFlow"] = item.DefaultFlow,
        };

    private static IReadOnlyDictionary<string, string?> CallQueueInventoryProperties(CallQueueInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["routing"] = item.Routing,
            ["alertTime"] = item.AlertTime,
            ["language"] = item.Language,
            ["agentCount"] = item.AgentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["overflowThreshold"] = item.OverflowThreshold,
            ["timeoutThreshold"] = item.TimeoutThreshold,
            ["overflowAction"] = item.OverflowAction,
            ["timeoutAction"] = item.TimeoutAction,
        };

    private static IReadOnlyDictionary<string, string?> ScheduleInventoryProperties(ScheduleInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["type"] = item.Type,
            ["dateCount"] = item.DateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static IReadOnlyDictionary<string, string?> PhoneNumberProperties(PhoneNumberInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["type"] = item.Type,
            ["assignedTo"] = item.AssignedTo,
            ["status"] = item.Status,
            ["activation"] = item.Activation,
            ["city"] = item.City,
            ["capability"] = item.Capability,
        };

    private static IReadOnlyDictionary<string, string?> VoiceUserProperties(VoiceUserInventorySnapshot item)
        => new Dictionary<string, string?>
        {
            ["name"] = item.Name,
            ["lineUri"] = item.LineUri,
            ["voiceRoutingPolicy"] = item.VoiceRoutingPolicy,
            ["callingPolicy"] = item.CallingPolicy,
            ["dialPlan"] = item.DialPlan,
        };

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
