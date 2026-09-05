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
            new[] { "Monday", "Sunday" },
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
}
