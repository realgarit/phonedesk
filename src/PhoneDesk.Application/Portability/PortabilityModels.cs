using PhoneDesk.Topology;

namespace PhoneDesk.Portability;

public static class PortabilitySchema
{
    public const int CurrentVersion = 1;
    public const int TopologyCurrentVersion = 2;
    public const int TopologyLegacyVersion = 1;
    public const string ConfigurationKind = "phonedesk.configuration";
    public const string TopologyKind = "phonedesk.topology";
}

public sealed record ConfigurationDocument(
    int SchemaVersion,
    string Kind,
    PortableConfiguration Configuration);

public sealed record PortableConfiguration(
    GeneralConfiguration General,
    AutoAttendantTemplate AutoAttendantTemplate,
    CallQueueTemplate CallQueueTemplate,
    BusinessHoursTemplate BusinessHoursTemplate,
    HolidayTemplate HolidayTemplate);

public sealed record GeneralConfiguration(
    string GroupName,
    string GroupDescription,
    string Customer,
    string CustomerGroupName,
    string MsFallbackDomain,
    string RaaAnrName,
    string CustomerLegalName,
    string LanguageId,
    string TimeZoneId,
    string UsageLocation,
    string SkuId,
    string CsAppCqId,
    string CsAppAaId,
    string RaaAnr,
    string PhoneNumberType,
    string M365GroupId);

public sealed record AutoAttendantTemplate(
    string? DefaultGreetingType,
    string? DefaultGreetingAudioFileId,
    string? DefaultGreetingTextToSpeechPrompt,
    string? DefaultAction,
    string? DefaultActionTarget,
    string? DefaultDisconnectAction,
    string? AfterHoursGreetingType,
    string? AfterHoursGreetingAudioFileId,
    string? AfterHoursGreetingTextToSpeechPrompt,
    string? AfterHoursAction,
    string? AfterHoursActionTarget,
    string? AfterHoursDisconnectAction);

public sealed record CallQueueTemplate(
    string? GreetingType,
    string? GreetingAudioFileId,
    string? GreetingTextToSpeechPrompt,
    string? MusicOnHoldType,
    string? MusicOnHoldAudioFileId,
    int? OverflowThreshold,
    string? OverflowAction,
    string? OverflowActionTarget,
    string? OverflowVoicemailGreetingType,
    string? OverflowActionAudioFileId,
    string? OverflowActionTextToSpeechPrompt,
    string? OverflowDisconnectAction,
    int? TimeoutThreshold,
    string? TimeoutAction,
    string? TimeoutActionTarget,
    string? TimeoutVoicemailGreetingType,
    string? TimeoutActionAudioFileId,
    string? TimeoutActionTextToSpeechPrompt,
    string? TimeoutDisconnectAction,
    string? NoAgentAction,
    string? NoAgentActionTarget,
    string? NoAgentVoicemailGreetingType,
    string? NoAgentActionAudioFileId,
    string? NoAgentActionTextToSpeechPrompt,
    string? NoAgentDisconnectAction,
    bool NoAgentApplyToNewCallsOnly);

public sealed record BusinessHoursTemplate(
    TimeOnly OpeningHours1Start,
    TimeOnly OpeningHours1End,
    TimeOnly OpeningHours2Start,
    TimeOnly OpeningHours2End,
    bool UsePerDaySchedule,
    IReadOnlyList<PortableDaySchedule> WeeklySchedule);

public sealed record PortableDaySchedule(
    string DayName,
    bool IsEnabled,
    TimeOnly Hours1Start,
    TimeOnly Hours1End,
    TimeOnly Hours2Start,
    TimeOnly Hours2End,
    bool HasSecondRange);

public sealed record HolidayTemplate(
    string NameSuffix,
    string GreetingPromptDe,
    DateOnly DefaultDate,
    TimeOnly DefaultTime,
    IReadOnlyList<PortableHolidayEntry> Series);

public sealed record PortableHolidayEntry(
    DateOnly Date,
    TimeOnly Time,
    string? Name,
    DateOnly? EndDate,
    TimeOnly? EndTime);

public sealed record ConfigurationChange(
    string Path,
    string CurrentValue,
    string ImportedValue)
{
    public string DisplayPath => Path.StartsWith("configuration.", StringComparison.Ordinal)
        ? Path["configuration.".Length..]
        : Path;
}

public sealed record TopologySnapshotDocument(
    int SchemaVersion,
    string Kind,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<TopologyAutoAttendantSnapshot> AutoAttendants,
    IReadOnlyList<TopologyCallQueueSnapshot> CallQueues,
    IReadOnlyList<TopologyResourceAccountSnapshot> ResourceAccounts,
    IReadOnlyList<TopologyGroupSnapshot> Groups,
    TenantDocumentationSnapshot? Documentation = null);

public sealed record TopologyAutoAttendantSnapshot(
    string Name,
    string Identity,
    string LanguageId,
    string TimeZoneId,
    IReadOnlyList<string> ResourceAccountObjectIds,
    IReadOnlyList<string> HolidayScheduleIds,
    IReadOnlyList<string> CallTargetObjectIds);

public sealed record TopologyCallQueueSnapshot(
    string Name,
    string Identity,
    string RoutingMethod,
    int AgentAlertTime,
    IReadOnlyList<string> AgentObjectIds,
    IReadOnlyList<string> DistributionListIds,
    IReadOnlyList<string> ResourceAccountObjectIds);

public sealed record TopologyResourceAccountSnapshot(
    string DisplayName,
    string UserPrincipalName,
    string ObjectId,
    string? PhoneNumber,
    ResourceAccountKind Kind,
    bool AccountEnabled);

public sealed record TopologyGroupSnapshot(
    string DisplayName,
    string Id,
    string MailNickname,
    string Description);

public sealed record TenantDocumentationRawData(
    string Tenant,
    string ResourceAccounts,
    string AutoAttendants,
    string CallQueues,
    string Schedules,
    string PhoneNumbers,
    string VoiceUsers);

public sealed record TenantDocumentationSnapshot(
    TenantInformationSnapshot Tenant,
    IReadOnlyList<ResourceAccountInventorySnapshot> ResourceAccounts,
    IReadOnlyList<AutoAttendantInventorySnapshot> AutoAttendants,
    IReadOnlyList<AutoAttendantMenuOptionSnapshot> AutoAttendantMenuOptions,
    IReadOnlyList<AutoAttendantCallFlowSnapshot> AutoAttendantCallFlows,
    IReadOnlyList<AutoAttendantScheduleAssociationSnapshot> AutoAttendantScheduleAssociations,
    IReadOnlyList<AutoAttendantOperatorSnapshot> AutoAttendantOperators,
    IReadOnlyList<CallQueueInventorySnapshot> CallQueues,
    IReadOnlyList<CallQueueAgentSnapshot> CallQueueAgents,
    IReadOnlyList<CallQueueDistributionListSnapshot> CallQueueDistributionLists,
    IReadOnlyList<QueueThresholdActionSnapshot> CallQueueActions,
    IReadOnlyList<ScheduleInventorySnapshot> Schedules,
    IReadOnlyList<ScheduleDateRangeSnapshot> ScheduleDateRanges,
    IReadOnlyList<ScheduleWeeklyRangeSnapshot> ScheduleWeeklyRanges,
    IReadOnlyList<PhoneNumberInventorySnapshot> PhoneNumbers,
    IReadOnlyList<VoiceUserInventorySnapshot> VoiceUsers);

public sealed record TenantInformationSnapshot(
    string Name,
    string Id,
    string Country,
    string Language);

public sealed record ResourceAccountInventorySnapshot(
    string Name,
    string UserPrincipalName,
    string ObjectId,
    string ApplicationId,
    string PhoneNumber,
    IReadOnlyList<ResourceAccountAssociationSnapshot> Associations);

public sealed record ResourceAccountAssociationSnapshot(
    string ConfigurationId,
    string ConfigurationType);

public sealed record AutoAttendantInventorySnapshot(
    string Name,
    string Identity,
    string Language,
    string TimeZone,
    string Voice,
    string DefaultFlow);

public sealed record AutoAttendantMenuOptionSnapshot(
    string AutoAttendantName,
    string? AutoAttendantIdentity,
    string FlowName,
    string Key,
    string Action,
    string TargetId,
    int Occurrence);

public sealed record AutoAttendantCallFlowSnapshot(
    string AutoAttendantName,
    string? AutoAttendantIdentity,
    string FlowName,
    string MenuName,
    int Occurrence);

public sealed record AutoAttendantScheduleAssociationSnapshot(
    string AutoAttendantName,
    string? AutoAttendantIdentity,
    string Type,
    string ScheduleId,
    string CallFlowId,
    int Occurrence);

public sealed record AutoAttendantOperatorSnapshot(
    string AutoAttendantName,
    string? AutoAttendantIdentity,
    string Type,
    string TargetId,
    int Occurrence);

public sealed record CallQueueInventorySnapshot(
    string Name,
    string Identity,
    string Routing,
    string AlertTime,
    string Language,
    int AgentCount,
    string OverflowThreshold,
    string TimeoutThreshold,
    string OverflowAction,
    string TimeoutAction);

public sealed record CallQueueAgentSnapshot(
    string CallQueueName,
    string? CallQueueIdentity,
    string ObjectId,
    string OptIn,
    string DisplayName,
    string UserPrincipalName,
    int Occurrence);

public sealed record CallQueueDistributionListSnapshot(
    string CallQueueName,
    string? CallQueueIdentity,
    string GroupId,
    int Occurrence);

public sealed record QueueThresholdActionSnapshot(
    string CallQueueName,
    string? CallQueueIdentity,
    string Kind,
    string Action,
    string TargetId,
    string Threshold,
    int Occurrence);

public sealed record ScheduleInventorySnapshot(
    string Name,
    string Id,
    string Type,
    int DateCount);

public sealed record ScheduleDateRangeSnapshot(
    string ScheduleName,
    string? ScheduleId,
    string Start,
    string End,
    int Occurrence);

public sealed record ScheduleWeeklyRangeSnapshot(
    string ScheduleName,
    string? ScheduleId,
    string Day,
    string Start,
    string End,
    int Occurrence);

public sealed record PhoneNumberInventorySnapshot(
    string Number,
    string Type,
    string AssignedTo,
    string Status,
    string Activation,
    string City,
    string Capability);

public sealed record VoiceUserInventorySnapshot(
    string Name,
    string UserPrincipalName,
    string LineUri,
    string VoiceRoutingPolicy,
    string CallingPolicy,
    string DialPlan);

public enum TopologyDriftKind
{
    Added,
    Removed,
    Changed,
}

public sealed record TopologyDriftEntry(
    TopologyDriftKind Kind,
    string ObjectType,
    string ObjectId,
    string DisplayName,
    string? PropertyName,
    string? SnapshotValue,
    string? LiveValue);
