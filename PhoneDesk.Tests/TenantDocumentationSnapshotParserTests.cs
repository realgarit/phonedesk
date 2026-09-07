using PhoneDesk.Portability;
using PhoneDesk.Services;

namespace PhoneDesk.Tests;

public sealed class TenantDocumentationSnapshotParserTests
{
    private readonly TenantDocumentationSnapshotParser _parser = new();

    [Fact]
    public void ParseCapturesEveryTenantReportDatasetAndNestedRelationship()
    {
        var snapshot = _parser.Parse(CreateRawData());

        Assert.Equal("tenant-1", snapshot.Tenant.Id);

        var resourceAccount = Assert.Single(snapshot.ResourceAccounts);
        Assert.Equal("ra-1", resourceAccount.ObjectId);
        var association = Assert.Single(resourceAccount.Associations);
        Assert.Equal("cq-1", association.ConfigurationId);
        Assert.Equal(0, association.Occurrence);

        var autoAttendant = Assert.Single(snapshot.AutoAttendants);
        Assert.Equal("aa-1", autoAttendant.Identity);
        var menuOption = Assert.Single(snapshot.AutoAttendantMenuOptions);
        Assert.Equal("aa-1", menuOption.AutoAttendantIdentity);
        Assert.Equal("cq-1", menuOption.TargetId);
        Assert.Equal("After hours", Assert.Single(snapshot.AutoAttendantCallFlows).FlowName);
        Assert.Equal("schedule-1", Assert.Single(snapshot.AutoAttendantScheduleAssociations).ScheduleId);
        Assert.Equal("operator-1", Assert.Single(snapshot.AutoAttendantOperators).TargetId);

        var callQueue = Assert.Single(snapshot.CallQueues);
        Assert.Equal("agent-1", Assert.Single(snapshot.CallQueueAgents).ObjectId);
        Assert.Equal("group-1", Assert.Single(snapshot.CallQueueDistributionLists).GroupId);
        Assert.Equal("voicemail-1", Assert.Single(snapshot.CallQueueActions, action => action.Kind == "overflow").TargetId);
        Assert.Equal("disconnect-1", Assert.Single(snapshot.CallQueueActions, action => action.Kind == "timeout").TargetId);

        var schedule = Assert.Single(snapshot.Schedules);
        Assert.Equal("schedule-1", schedule.Id);
        Assert.Single(snapshot.ScheduleDateRanges);
        Assert.Single(snapshot.ScheduleWeeklyRanges);

        Assert.Equal("+41440000000", Assert.Single(snapshot.PhoneNumbers).Number);
        Assert.Equal("user@contoso.example", Assert.Single(snapshot.VoiceUsers).UserPrincipalName);
    }

    [Fact]
    public void ParseRejectsNestedRowsWhoseParentWasNotCaptured()
    {
        var raw = CreateRawData() with
        {
            AutoAttendants =
                "DOCDATA_AA_START\n" +
                "DOCDATA_AA_MENU: Missing AA|DefaultCallFlow|1|Transfer|cq-1\n" +
                "DOCDATA_AA_END"
        };

        var error = Assert.Throws<InvalidDataException>(() => _parser.Parse(raw));

        Assert.Contains("unknown parent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsePreservesDuplicateParentNamesWithoutDuplicatingChildRows()
    {
        var raw = CreateRawData() with
        {
            AutoAttendants =
                "DOCDATA_AA_START\n" +
                "DOCDATA_AA: Reception|aa-1|de-DE|Europe/Zurich|Female|Default\n" +
                "DOCDATA_AA: Reception|aa-2|en-US|Europe/London|Male|Default\n" +
                "DOCDATA_AA_MENU: Reception|DefaultCallFlow|1|Transfer|cq-1\n" +
                "DOCDATA_AA_END"
        };

        var snapshot = _parser.Parse(raw);

        Assert.Equal(new[] { "aa-1", "aa-2" }, snapshot.AutoAttendants.Select(item => item.Identity));
        var menu = Assert.Single(snapshot.AutoAttendantMenuOptions);
        Assert.Equal("Reception", menu.AutoAttendantName);
        Assert.Null(menu.AutoAttendantIdentity);
        Assert.Equal(0, menu.Occurrence);
    }

    internal static TenantDocumentationRawData CreateRawData()
        => new(
            "DOCDATA_TENANT: Contoso|tenant-1|CH|de-CH\nSUCCESS: Tenant info exported",
            "DOCDATA_RA_START\n" +
            "DOCDATA_RA: Reception RA|reception@contoso.example|ra-1|cq-app|+41440000000\n" +
            "DOCDATA_RA_END\n" +
            "DOCDATA_ASSOC_START\n" +
            "DOCDATA_ASSOC: Reception RA|ra-1|cq-1|CallQueue\n" +
            "DOCDATA_ASSOC_END",
            "DOCDATA_AA_START\n" +
            "DOCDATA_AA: Reception|aa-1|de-DE|Europe/Zurich|Female|Default\n" +
            "DOCDATA_AA_MENU: Reception|DefaultCallFlow|1|Transfer|cq-1\n" +
            "DOCDATA_AA_CF: Reception|After hours|After-hours menu\n" +
            "DOCDATA_AA_CHA: Reception|AfterHours|schedule-1|flow-1\n" +
            "DOCDATA_AA_OP: Reception|ExternalPstn|operator-1\n" +
            "DOCDATA_AA_END",
            "DOCDATA_CQ_START\n" +
            "DOCDATA_CQ: Support|cq-1|LongestIdle|20|de-DE|1|10|30|Voicemail|Disconnect\n" +
            "DOCDATA_CQ_AGENT: Support|agent-1|True|Ada Lovelace|ada@contoso.example\n" +
            "DOCDATA_CQ_DL: Support|group-1\n" +
            "DOCDATA_CQ_OVERFLOW: Support|Voicemail|voicemail-1|10\n" +
            "DOCDATA_CQ_TIMEOUT: Support|Disconnect|disconnect-1|30\n" +
            "DOCDATA_CQ_END",
            "DOCDATA_SCHED_START\n" +
            "DOCDATA_SCHED: Business hours|schedule-1|Weekly|1\n" +
            "DOCDATA_SCHED_DR: Business hours|2026-12-24T12:00:00|2026-12-27T08:00:00\n" +
            "DOCDATA_SCHED_WK: Business hours|Monday|08:00:00|17:00:00\n" +
            "DOCDATA_SCHED_END",
            "DOCDATA_PHONE_START\n" +
            "DOCDATA_PHONE: +41440000000|DirectRouting|ra-1|Assigned|Activated|Zurich|Voice\n" +
            "DOCDATA_PHONE_END",
            "DOCDATA_USER_START\n" +
            "DOCDATA_USER: Ada Lovelace|user@contoso.example|tel:+41440000001|Swiss|AllowCalling|Global\n" +
            "DOCDATA_USER_END");
}
