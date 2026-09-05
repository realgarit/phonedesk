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
        var document = CreateTopologyDocument(topology);
        return NormalizeNewlines(JsonSerializer.Serialize(document, JsonOptions));
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
        ValidateTopology(snapshot);
        snapshot = NormalizeTopology(snapshot);
        var live = CreateTopologyDocument(liveTopology);
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

    private static TopologySnapshotDocument CreateTopologyDocument(TenantTopology topology)
    {
        if (topology.AutoAttendants is null || topology.CallQueues is null ||
            topology.ResourceAccounts is null || topology.Groups is null)
        {
            throw new InvalidDataException("The live topology is missing a required collection.");
        }

        var document = new TopologySnapshotDocument(
            PortabilitySchema.CurrentVersion,
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
                item.Description)).ToArray());
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
                .ToArray()
        };

    private static IReadOnlyList<string> Sort(IReadOnlyList<string>? values)
        => (values ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static void ValidateTopology(TopologySnapshotDocument document)
    {
        if (document.SchemaVersion != PortabilitySchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported topology schema version {document.SchemaVersion}; expected {PortabilitySchema.CurrentVersion}.");
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
    }

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

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
