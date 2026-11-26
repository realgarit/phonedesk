using PhoneDesk.Models;
using PhoneDesk.Portability;
using PhoneDesk.Services;

namespace PhoneDesk.Operations;

/// <summary>
/// Read-only configuration view over a portable configuration document. It gives a head without
/// a UI model the same <see cref="IPhoneManagerVariables"/> contract that validation and the
/// script builders consume, from the same JSON document the desktop app exports and imports.
/// </summary>
public sealed class ConfigurationDocumentVariables : IPhoneManagerVariables
{
    private readonly GeneralConfiguration _general;
    private readonly AutoAttendantTemplate _autoAttendant;
    private readonly CallQueueTemplate _callQueue;
    private readonly BusinessHoursTemplate _hours;
    private readonly HolidayTemplate _holiday;

    public ConfigurationDocumentVariables(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var configuration = document.Configuration
            ?? throw new InvalidDataException("The configuration payload is missing.");
        _general = configuration.General;
        _autoAttendant = configuration.AutoAttendantTemplate;
        _callQueue = configuration.CallQueueTemplate;
        _hours = configuration.BusinessHoursTemplate;
        _holiday = configuration.HolidayTemplate;

        WeeklySchedule = _hours.WeeklySchedule
            .Select(day => (IDaySchedule)new DayScheduleValue(
                day.DayName,
                day.IsEnabled,
                day.Hours1Start.ToTimeSpan(),
                day.Hours1End.ToTimeSpan(),
                day.Hours2Start.ToTimeSpan(),
                day.Hours2End.ToTimeSpan(),
                day.HasSecondRange))
            .ToArray();
        HolidaySeries = _holiday.Series
            .Select(entry => (IHolidayEntry)new HolidayEntryValue(
                entry.Date.ToDateTime(TimeOnly.MinValue),
                entry.Time.ToTimeSpan(),
                entry.Name,
                entry.EndDate?.ToDateTime(TimeOnly.MinValue),
                entry.EndTime?.ToTimeSpan()))
            .ToArray();
    }

    /// <summary>The holiday dates of the configuration, for the holiday series script.</summary>
    public IReadOnlyList<IHolidayEntry> HolidaySeries { get; }

    /// <summary>The object ID of the agents' Microsoft 365 group, when it is already known.</summary>
    public string M365GroupId => _general.M365GroupId;

    public string GroupName => _general.GroupName;
    public string GroupDescription => _general.GroupDescription;
    public string RaaAnrName => _general.RaaAnrName;

    public string Customer => _general.Customer;
    public string CustomerGroupName => _general.CustomerGroupName;
    public string CustomerLegalName => _general.CustomerLegalName;
    public string MsFallbackDomain => _general.MsFallbackDomain;
    public string LanguageId => _general.LanguageId;
    public string TimeZoneId => _general.TimeZoneId;
    public string UsageLocation => _general.UsageLocation;
    public string RaaAnr => _general.RaaAnr;
    public string PhoneNumberType => _general.PhoneNumberType;
    public string SkuId => _general.SkuId;
    public string CsAppCqId => _general.CsAppCqId;
    public string CsAppAaId => _general.CsAppAaId;

    public string M365Group => ResourceNames.M365Group(Customer, CustomerGroupName);
    public string RacqUPN => ResourceAccountUpn.Build(RacqDisplayName, MsFallbackDomain);
    public string RacqDisplayName => ResourceNames.CallQueueResourceAccount(Customer, CustomerGroupName);
    public string CqDisplayName => ResourceNames.CallQueue(Customer, CustomerGroupName);
    public string RaaaUPN => ResourceAccountUpn.Build(RaaaDisplayName, MsFallbackDomain);
    public string RaaaDisplayName => ResourceNames.AutoAttendantResourceAccount(Customer, RaaAnrName, CustomerGroupName);
    public string AaDisplayName => ResourceNames.AutoAttendant(Customer, RaaAnrName, CustomerGroupName);
    public string HolidayName => ResourceNames.Holiday(Customer, HolidayNameSuffix);

    public string HolidayNameSuffix => _holiday.NameSuffix;
    public string HolidayGreetingPromptDE => _holiday.GreetingPromptDe;

    public string? AaDefaultGreetingType => _autoAttendant.DefaultGreetingType;
    public string? AaDefaultGreetingAudioFileId => _autoAttendant.DefaultGreetingAudioFileId;
    public string? AaDefaultGreetingTextToSpeechPrompt => _autoAttendant.DefaultGreetingTextToSpeechPrompt;
    public string? AaDefaultAction => _autoAttendant.DefaultAction;
    public string? AaDefaultActionTarget => _autoAttendant.DefaultActionTarget;

    public string? AaAfterHoursGreetingType => _autoAttendant.AfterHoursGreetingType;
    public string? AaAfterHoursGreetingAudioFileId => _autoAttendant.AfterHoursGreetingAudioFileId;
    public string? AaAfterHoursGreetingTextToSpeechPrompt => _autoAttendant.AfterHoursGreetingTextToSpeechPrompt;
    public string? AaAfterHoursAction => _autoAttendant.AfterHoursAction;
    public string? AaAfterHoursActionTarget => _autoAttendant.AfterHoursActionTarget;

    public TimeSpan OpeningHours1Start => _hours.OpeningHours1Start.ToTimeSpan();
    public TimeSpan OpeningHours1End => _hours.OpeningHours1End.ToTimeSpan();
    public TimeSpan OpeningHours2Start => _hours.OpeningHours2Start.ToTimeSpan();
    public TimeSpan OpeningHours2End => _hours.OpeningHours2End.ToTimeSpan();
    public bool UsePerDaySchedule => _hours.UsePerDaySchedule;
    public IReadOnlyList<IDaySchedule> WeeklySchedule { get; }

    public string? CqGreetingType => _callQueue.GreetingType;
    public string? CqGreetingAudioFileId => _callQueue.GreetingAudioFileId;
    public string? CqGreetingTextToSpeechPrompt => _callQueue.GreetingTextToSpeechPrompt;
    public string? CqMusicOnHoldType => _callQueue.MusicOnHoldType;
    public string? CqMusicOnHoldAudioFileId => _callQueue.MusicOnHoldAudioFileId;

    public int? CqOverflowThreshold => _callQueue.OverflowThreshold;
    public string? CqOverflowAction => _callQueue.OverflowAction;
    public string? CqOverflowActionTarget => _callQueue.OverflowActionTarget;
    public string? CqOverflowVoicemailGreetingType => _callQueue.OverflowVoicemailGreetingType;
    public string? CqOverflowActionAudioFileId => _callQueue.OverflowActionAudioFileId;
    public string? CqOverflowActionTextToSpeechPrompt => _callQueue.OverflowActionTextToSpeechPrompt;

    public int? CqTimeoutThreshold => _callQueue.TimeoutThreshold;
    public string? CqTimeoutAction => _callQueue.TimeoutAction;
    public string? CqTimeoutActionTarget => _callQueue.TimeoutActionTarget;
    public string? CqTimeoutVoicemailGreetingType => _callQueue.TimeoutVoicemailGreetingType;
    public string? CqTimeoutActionAudioFileId => _callQueue.TimeoutActionAudioFileId;
    public string? CqTimeoutActionTextToSpeechPrompt => _callQueue.TimeoutActionTextToSpeechPrompt;

    public string? CqNoAgentAction => _callQueue.NoAgentAction;
    public string? CqNoAgentActionTarget => _callQueue.NoAgentActionTarget;
    public string? CqNoAgentVoicemailGreetingType => _callQueue.NoAgentVoicemailGreetingType;
    public string? CqNoAgentActionAudioFileId => _callQueue.NoAgentActionAudioFileId;
    public string? CqNoAgentActionTextToSpeechPrompt => _callQueue.NoAgentActionTextToSpeechPrompt;
    public bool CqNoAgentApplyToNewCallsOnly => _callQueue.NoAgentApplyToNewCallsOnly;
    public bool CqUseAutoAttendantNumberAsCallerId => _callQueue.UseAutoAttendantNumberAsCallerId;

    /// <summary>
    /// An empty configuration with the same defaults as a new configuration in the desktop app:
    /// Teams Phone resource account SKU and application IDs, office hours 08:00 to 17:00, and a
    /// Monday to Friday day plan.
    /// </summary>
    public static ConfigurationDocument CreateDefaultDocument(DateOnly today)
    {
        static PortableDaySchedule Day(string name, bool enabled) => new(
            name, enabled, new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), false);

        return new ConfigurationDocument(
            PortabilitySchema.CurrentVersion,
            PortabilitySchema.ConfigurationKind,
            new PortableConfiguration(
                new GeneralConfiguration(
                    GroupName: string.Empty,
                    GroupDescription: string.Empty,
                    Customer: string.Empty,
                    CustomerGroupName: string.Empty,
                    MsFallbackDomain: string.Empty,
                    RaaAnrName: string.Empty,
                    CustomerLegalName: string.Empty,
                    LanguageId: string.Empty,
                    TimeZoneId: string.Empty,
                    UsageLocation: string.Empty,
                    SkuId: ConstantsService.TeamsPhone.SkuId,
                    CsAppCqId: ConstantsService.TeamsPhone.CallQueueAppId,
                    CsAppAaId: ConstantsService.TeamsPhone.AutoAttendantAppId,
                    RaaAnr: string.Empty,
                    PhoneNumberType: string.Empty,
                    M365GroupId: string.Empty),
                new AutoAttendantTemplate(null, null, null, ConfigurationChecks.TransferToCallQueue, null, null, null, null, null, null, null, null),
                new CallQueueTemplate(
                    null, null, null, null, null,
                    null, null, null, null, null, null, null,
                    null, null, null, null, null, null, null,
                    null, null, null, null, null, null, false),
                new BusinessHoursTemplate(
                    new TimeOnly(8, 0),
                    new TimeOnly(17, 0),
                    TimeOnly.MinValue,
                    TimeOnly.MinValue,
                    false,
                    new[]
                    {
                        Day("Monday", true),
                        Day("Tuesday", true),
                        Day("Wednesday", true),
                        Day("Thursday", true),
                        Day("Friday", true),
                        Day("Saturday", false),
                        Day("Sunday", false)
                    }),
                new HolidayTemplate(
                    string.Empty,
                    string.Empty,
                    today,
                    new TimeOnly(9, 0),
                    Array.Empty<PortableHolidayEntry>())));
    }

    private sealed record DayScheduleValue(
        string DayName,
        bool IsEnabled,
        TimeSpan Hours1Start,
        TimeSpan Hours1End,
        TimeSpan Hours2Start,
        TimeSpan Hours2End,
        bool HasSecondRange) : IDaySchedule
    {
        public bool HasEffectiveSecondRange => HasSecondRange && Hours2Start != Hours2End;
    }

    private sealed record HolidayEntryValue(
        DateTime Date,
        TimeSpan Time,
        string? Name,
        DateTime? EndDate,
        TimeSpan? EndTime) : IHolidayEntry
    {
        public DateTime DateTime => Date.Date.Add(Time);
        public DateTime? EndDateTime => EndDate?.Date.Add(EndTime ?? TimeSpan.Zero);
        public bool HasEndDate => EndDate.HasValue;
    }
}
