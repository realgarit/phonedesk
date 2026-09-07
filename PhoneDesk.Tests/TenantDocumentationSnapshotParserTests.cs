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
        Assert.Equal("cq-1", Assert.Single(resourceAccount.Associations).ConfigurationId);

        var autoAttendant = Assert.Single(snapshot.AutoAttendants);
        Assert.Equal("aa-1", autoAttendant.Identity);
        Assert.Equal("cq-1", Assert.Single(autoAttendant.MenuOptions).TargetId);
        Assert.Equal("After hours", Assert.Single(autoAttendant.CallFlows).FlowName);
        Assert.Equal("schedule-1", Assert.Single(autoAttendant.ScheduleAssociations).ScheduleId);
        Assert.Equal("operator-1", Assert.IsType<AutoAttendantOperatorSnapshot>(autoAttendant.Operator).TargetId);

        var callQueue = Assert.Single(snapshot.CallQueues);
        Assert.Equal("agent-1", Assert.Single(callQueue.Agents).ObjectId);
        Assert.Equal("group-1", Assert.Single(callQueue.DistributionListIds));
        Assert.Equal("voicemail-1", Assert.IsType<QueueThresholdActionSnapshot>(callQueue.Overflow).TargetId);
        Assert.Equal("disconnect-1", Assert.IsType<QueueThresholdActionSnapshot>(callQueue.Timeout).TargetId);

        var schedule = Assert.Single(snapshot.Schedules);
        Assert.Equal("schedule-1", schedule.Id);
        Assert.Single(schedule.DateRanges);
        Assert.Single(schedule.WeeklyRanges);

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
