using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Topology;

namespace PhoneDesk.Tests;

public sealed class TenantAsCodeServiceTests
{
    private readonly TenantAsCodeService _service = new();

    [Fact]
    public void ConfigurationJsonIsDeterministicStableOrderedAndSecretFree()
    {
        var document = CreateConfigurationDocument();

        var first = _service.SerializeConfiguration(document);
        var second = _service.SerializeConfiguration(document);
        var roundTrip = _service.DeserializeConfiguration(first);

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r\n", first);
        Assert.DoesNotContain("password", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", first, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
            roundTrip.Configuration.BusinessHoursTemplate.WeeklySchedule.Select(day => day.DayName));
        Assert.Equal(
            new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25) },
            roundTrip.Configuration.HolidayTemplate.Series.Select(entry => entry.Date));
    }

    [Fact]
    public void ConfigurationParserRejectsUnknownFieldsWrongKindAndUnsupportedVersion()
    {
        var valid = _service.SerializeConfiguration(CreateConfigurationDocument());
        var unknown = valid.Replace(
            "\"configuration\": {",
            "\"unexpected\": true,\n  \"configuration\": {",
            StringComparison.Ordinal);
        var wrongKind = valid.Replace(PortabilitySchema.ConfigurationKind, "phonedesk.other", StringComparison.Ordinal);
        var wrongVersion = valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => _service.DeserializeConfiguration(unknown));
        Assert.Throws<InvalidDataException>(() => _service.DeserializeConfiguration(wrongKind));
        Assert.Throws<InvalidDataException>(() => _service.DeserializeConfiguration(wrongVersion));
    }

    [Fact]
    public void ConfigurationSerializerRejectsNullScheduleEntriesAsInvalidSchema()
    {
        var document = CreateConfigurationDocument();
        document = document with
        {
            Configuration = document.Configuration with
            {
                BusinessHoursTemplate = document.Configuration.BusinessHoursTemplate with
                {
                    WeeklySchedule = new PortableDaySchedule[1]
                }
            }
        };

        Assert.Throws<InvalidDataException>(() => _service.SerializeConfiguration(document));
    }

    [Fact]
    public void ConfigurationImportRejectsMissingWeekdays()
    {
        var json = _service.SerializeConfiguration(CreateConfigurationDocument());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.NotNull(node);
        var schedule = node["configuration"]?["businessHoursTemplate"]?["weeklySchedule"]?.AsArray();
        Assert.NotNull(schedule);
        schedule.RemoveAt(0);
        var exception = Assert.Throws<InvalidDataException>(() => _service.DeserializeConfiguration(node.ToJsonString()));
        Assert.Contains("all seven days", exception.Message);
    }

    [Fact]
    public void ConfigurationRoundTripsHolidayEndDateWithDefaultMidnightEndTime()
    {
        var document = CreateConfigurationDocument();
        document = document with
        {
            Configuration = document.Configuration with
            {
                HolidayTemplate = document.Configuration.HolidayTemplate with
                {
                    Series = new[]
                    {
                        new PortableHolidayEntry(
                            new DateOnly(2026, 12, 24),
                            new TimeOnly(12, 0),
                            "Christmas closure",
                            new DateOnly(2026, 12, 27),
                            null)
                    }
                }
            }
        };

        var roundTrip = _service.DeserializeConfiguration(_service.SerializeConfiguration(document));

        var holiday = Assert.Single(roundTrip.Configuration.HolidayTemplate.Series);
        Assert.Equal(new DateOnly(2026, 12, 27), holiday.EndDate);
        Assert.Null(holiday.EndTime);
    }

    [Fact]
    public void ConfigurationComparisonReturnsOnlyPropertyLevelChanges()
    {
        var current = CreateConfigurationDocument();
        var imported = current with
        {
            Configuration = current.Configuration with
            {
                General = current.Configuration.General with { Customer = "Fabrikam" },
                CallQueueTemplate = current.Configuration.CallQueueTemplate with { OverflowThreshold = 42 }
            }
        };

        var changes = _service.CompareConfigurations(current, imported);

        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal("configuration.general.customer", change.Path);
                Assert.Equal("Contoso", change.CurrentValue);
                Assert.Equal("Fabrikam", change.ImportedValue);
            },
            change =>
            {
                Assert.Equal("configuration.callQueueTemplate.overflowThreshold", change.Path);
                Assert.Equal("10", change.CurrentValue);
                Assert.Equal("42", change.ImportedValue);
            });
    }

    [Fact]
    public void TopologyJsonIsStableOrderedAndRoundTrips()
    {
        var topology = CreateLiveTopology();

        var json = _service.SerializeTopology(topology);
        var roundTrip = _service.DeserializeTopology(json);

        Assert.Equal("aa-1", roundTrip.AutoAttendants[0].Identity);
        Assert.Equal(new[] { "ra-1", "ra-2" }, roundTrip.AutoAttendants[0].ResourceAccountObjectIds);
        Assert.Equal("cq-1", roundTrip.CallQueues[0].Identity);
        Assert.Equal("ra-1", roundTrip.ResourceAccounts[0].ObjectId);
        Assert.Equal("group-1", roundTrip.Groups[0].Id);
        Assert.Equal(json, _service.SerializeTopology(topology));
    }

    [Fact]
    public void LegacyTopologyWithoutDocumentationRemainsReadable()
    {
        var json = _service.SerializeTopology(CreateLiveTopology());
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(root);
        root.Remove("documentation");

        var roundTrip = _service.DeserializeTopology(root.ToJsonString());

        Assert.Equal(PortabilitySchema.TopologyLegacyVersion, roundTrip.SchemaVersion);
        Assert.Null(roundTrip.Documentation);
        Assert.Single(roundTrip.AutoAttendants);
    }

    [Fact]
    public void FullTopologySnapshotIsSchemaVersionTwoAndContainsTenantReportSuperset()
    {
        var topology = CreateLiveTopology();
        var documentation = new TenantDocumentationSnapshotParser().Parse(
            TenantDocumentationSnapshotParserTests.CreateRawData());
        var document = _service.CreateTopologySnapshot(topology, documentation);

        var first = _service.SerializeTopology(document);
        var roundTrip = _service.DeserializeTopology(first);
        var second = _service.SerializeTopology(roundTrip);

        Assert.Equal(PortabilitySchema.TopologyCurrentVersion, roundTrip.SchemaVersion);
        Assert.NotNull(roundTrip.Documentation);
        Assert.Single(roundTrip.Documentation.PhoneNumbers);
        Assert.Single(roundTrip.Documentation.VoiceUsers);
        Assert.Single(roundTrip.Documentation.Schedules);
        Assert.Contains("\"documentation\"", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.DoesNotContain("password", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullTopologySerializationStableOrdersTopLevelAndNestedInventory()
    {
        var topology = CreateLiveTopology();
        var parsed = new TenantDocumentationSnapshotParser().Parse(
            TenantDocumentationSnapshotParserTests.CreateRawData());
        var secondPhone = new PhoneNumberInventorySnapshot(
            "+41310000000", "CallingPlan", "user-2", "Assigned", "Activated", "Bern", "Voice");
        var secondAgent = new CallQueueAgentSnapshot(
            "Support", "cq-1", "agent-0", "False", "Grace Hopper", "grace@contoso.example", 0);
        var forward = parsed with
        {
            PhoneNumbers = parsed.PhoneNumbers.Append(secondPhone).ToArray(),
            CallQueueAgents = parsed.CallQueueAgents.Append(secondAgent).ToArray()
        };
        var reversed = forward with
        {
            PhoneNumbers = forward.PhoneNumbers.Reverse().ToArray(),
            CallQueueAgents = forward.CallQueueAgents.Reverse().ToArray()
        };

        var firstJson = _service.SerializeTopology(_service.CreateTopologySnapshot(topology, forward));
        var secondJson = _service.SerializeTopology(_service.CreateTopologySnapshot(topology, reversed));

        Assert.Equal(firstJson, secondJson);
        var normalized = _service.DeserializeTopology(firstJson);
        Assert.Equal(
            new[] { "+41310000000", "+41440000000" },
            normalized.Documentation!.PhoneNumbers.Select(number => number.Number));
        Assert.Equal(
            new[] { "agent-0", "agent-1" },
            normalized.Documentation.CallQueueAgents.Select(agent => agent.ObjectId));
    }

    [Fact]
    public void FullTopologyComparisonIncludesInventoryAndDetailedCallFlowProperties()
    {
        var topology = CreateLiveTopology();
        var parser = new TenantDocumentationSnapshotParser();
        var savedDocumentation = parser.Parse(TenantDocumentationSnapshotParserTests.CreateRawData());
        var liveDocumentation = savedDocumentation with
        {
            AutoAttendants = savedDocumentation.AutoAttendants
                .Select(item => item with
                {
                    Voice = "Male"
                })
                .ToArray(),
            AutoAttendantMenuOptions = savedDocumentation.AutoAttendantMenuOptions
                .Select(option => option with { TargetId = "cq-2" })
                .ToArray(),
            PhoneNumbers = savedDocumentation.PhoneNumbers
                .Select(item => item with { City = "Bern" })
                .ToArray()
        };
        var saved = _service.CreateTopologySnapshot(topology, savedDocumentation);
        var live = _service.CreateTopologySnapshot(topology, liveDocumentation);

        var drift = _service.CompareTopology(saved, live);

        Assert.Contains(drift, item =>
            item.ObjectType == "autoAttendantInventory" &&
            item.ObjectId == "aa-1" &&
            item.PropertyName == "voice" &&
            item.SnapshotValue == "Female" &&
            item.LiveValue == "Male");
        Assert.Contains(drift, item =>
            item.ObjectType == "phoneNumber" &&
            item.ObjectId == "+41440000000" &&
            item.PropertyName == "city" &&
            item.SnapshotValue == "Zurich" &&
            item.LiveValue == "Bern");
        Assert.Contains(drift, item =>
            item.ObjectType == "autoAttendantMenuOption" &&
            item.PropertyName == "targetId" &&
            item.SnapshotValue == "cq-1" &&
            item.LiveValue == "cq-2");
    }

    [Fact]
    public void AmbiguousDuplicateNameDetailsUseAddedRemovedRowsInsteadOfMisattributedChanges()
    {
        var parser = new TenantDocumentationSnapshotParser();
        var baseRaw = TenantDocumentationSnapshotParserTests.CreateRawData();
        var savedRaw = baseRaw with
        {
            AutoAttendants = DuplicateNameAutoAttendantRows("cq-1", "cq-2")
        };
        var liveRaw = baseRaw with
        {
            AutoAttendants = DuplicateNameAutoAttendantRows("cq-1", "cq-3")
        };
        var topology = CreateLiveTopology();
        var saved = _service.CreateTopologySnapshot(topology, parser.Parse(savedRaw));
        var live = _service.CreateTopologySnapshot(topology, parser.Parse(liveRaw));

        var menuDrift = _service.CompareTopology(saved, live)
            .Where(item => item.ObjectType == "autoAttendantMenuOption")
            .ToArray();

        Assert.Contains(menuDrift, item =>
            item.Kind == TopologyDriftKind.Removed && item.ObjectId.Contains("cq-2", StringComparison.Ordinal));
        Assert.Contains(menuDrift, item =>
            item.Kind == TopologyDriftKind.Added && item.ObjectId.Contains("cq-3", StringComparison.Ordinal));
        Assert.DoesNotContain(menuDrift, item => item.Kind == TopologyDriftKind.Changed);
    }

    [Fact]
    public void TopologyComparisonReportsAddedRemovedAndChangedProperties()
    {
        var live = CreateLiveTopology();
        var snapshot = _service.DeserializeTopology(_service.SerializeTopology(live));
        snapshot = snapshot with
        {
            AutoAttendants = snapshot.AutoAttendants
                .Select(item => item.Identity == "aa-1" ? item with { LanguageId = "de-DE" } : item)
                .ToArray(),
            ResourceAccounts = snapshot.ResourceAccounts
                .Where(item => item.ObjectId != "ra-2")
                .Append(new TopologyResourceAccountSnapshot(
                    "Removed RA", "removed@contoso.example", "ra-removed", null,
                    ResourceAccountKind.CallQueue, true))
                .ToArray()
        };

        var drift = _service.CompareTopology(snapshot, live);

        Assert.Contains(drift, item =>
            item.Kind == TopologyDriftKind.Changed &&
            item.ObjectType == "autoAttendant" &&
            item.ObjectId == "aa-1" &&
            item.PropertyName == "languageId" &&
            item.SnapshotValue == "de-DE" &&
            item.LiveValue == "en-US");
        Assert.Contains(drift, item =>
            item.Kind == TopologyDriftKind.Added && item.ObjectId == "ra-2");
        Assert.Contains(drift, item =>
            item.Kind == TopologyDriftKind.Removed && item.ObjectId == "ra-removed");
    }

    [Fact]
    public void TopologySerializerRejectsDuplicateStableIdentifiers()
    {
        var topology = CreateLiveTopology();
        var duplicate = new TenantTopology(
            topology.AutoAttendants.Concat(topology.AutoAttendants).ToArray(),
            topology.CallQueues,
            topology.ResourceAccounts,
            topology.Groups,
            topology.Orphans,
            topology.RetrievedAtUtc);

        Assert.Throws<InvalidDataException>(() => _service.SerializeTopology(duplicate));
    }

    private static ConfigurationDocument CreateConfigurationDocument()
    {
        var configuration = new PortableConfiguration(
            new GeneralConfiguration(
                "Support", "Support group", "Contoso", "Helpdesk", "@contoso.example", "Main",
                "Contoso AG", "en-US", "Europe/Zurich", "CH", "sku", "cq-app", "aa-app",
                "+41440000000", "CallingPlan", "group-1"),
            new AutoAttendantTemplate(
                "TextToSpeech", null, "Welcome", "TransferToTarget", "cq-1", null,
                "TextToSpeech", null, "Closed", "Disconnect", null, "TextToSpeech"),
            new CallQueueTemplate(
                "TextToSpeech", null, "Please wait", "Default", null,
                10, "Disconnect", null, null, null, null, "TextToSpeech",
                60, "TransferToTarget", "group-1", null, null, null, null,
                "QueueCall", null, null, null, null, null, true),
            new BusinessHoursTemplate(
                new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), true,
                new[]
                {
                    new PortableDaySchedule("Sunday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                    new PortableDaySchedule("Monday", true, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), true),
                    new PortableDaySchedule("Saturday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                    new PortableDaySchedule("Friday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                    new PortableDaySchedule("Thursday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                    new PortableDaySchedule("Wednesday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                    new PortableDaySchedule("Tuesday", false, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false),
                }),
            new HolidayTemplate(
                "holidays", "We are closed", new DateOnly(2026, 1, 1), new TimeOnly(9, 0),
                new[]
                {
                    new PortableHolidayEntry(new DateOnly(2026, 12, 25), new TimeOnly(0, 0), "Christmas", null, null),
                    new PortableHolidayEntry(new DateOnly(2026, 1, 1), new TimeOnly(0, 0), "New Year", null, null),
                }));
        return new ConfigurationDocument(
            PortabilitySchema.CurrentVersion,
            PortabilitySchema.ConfigurationKind,
            configuration);
    }

    private static TenantTopology CreateLiveTopology()
        => new(
            new[]
            {
                new TopologyAutoAttendant(
                    "Main AA", "aa-1", "en-US", "Europe/Zurich",
                    new[] { "ra-2", "ra-1" }, new[] { "holiday-1" }, new[] { "cq-1" }),
            },
            new[]
            {
                new TopologyCallQueue(
                    "Support CQ", "cq-1", "Attendant", 30,
                    new[] { "agent-2", "agent-1" }, new[] { "group-1" }, new[] { "ra-2" }),
            },
            new[]
            {
                new TopologyResourceAccount("Queue RA", "queue@contoso.example", "ra-2", null, ResourceAccountKind.CallQueue, true),
                new TopologyResourceAccount("Main RA", "main@contoso.example", "ra-1", "+41440000000", ResourceAccountKind.AutoAttendant, true),
            },
            new[]
            {
                new TopologyGroup("Support", "group-1", "support", "Support team"),
            },
            Array.Empty<OrphanFinding>(),
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));

    private static string DuplicateNameAutoAttendantRows(string firstTarget, string secondTarget)
        => "DOCDATA_AA_START\n" +
           "DOCDATA_AA: Reception|aa-1|de-DE|Europe/Zurich|Female|Default\n" +
           "DOCDATA_AA: Reception|aa-2|en-US|Europe/London|Male|Default\n" +
           $"DOCDATA_AA_MENU: Reception|DefaultCallFlow|1|Transfer|{firstTarget}\n" +
           $"DOCDATA_AA_MENU: Reception|DefaultCallFlow|1|Transfer|{secondTarget}\n" +
           "DOCDATA_AA_END";
}
