using System.Collections.ObjectModel;
using PhoneDesk.Models;

namespace PhoneDesk.Portability;

public static class PhoneManagerVariablesPortabilityMapper
{
    public static ConfigurationDocument ToDocument(PhoneManagerVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return new ConfigurationDocument(
            PortabilitySchema.CurrentVersion,
            PortabilitySchema.ConfigurationKind,
            new PortableConfiguration(
                new GeneralConfiguration(
                    variables.GroupName,
                    variables.GroupDescription,
                    variables.Customer,
                    variables.CustomerGroupName,
                    variables.MsFallbackDomain,
                    variables.RaaAnrName,
                    variables.CustomerLegalName,
                    variables.LanguageId,
                    variables.TimeZoneId,
                    variables.UsageLocation,
                    variables.SkuId,
                    variables.CsAppCqId,
                    variables.CsAppAaId,
                    variables.RaaAnr,
                    variables.PhoneNumberType,
                    variables.M365GroupId),
                new AutoAttendantTemplate(
                    variables.AaDefaultGreetingType,
                    variables.AaDefaultGreetingAudioFileId,
                    variables.AaDefaultGreetingTextToSpeechPrompt,
                    variables.AaDefaultAction,
                    variables.AaDefaultActionTarget,
                    variables.AaDefaultDisconnectAction,
                    variables.AaAfterHoursGreetingType,
                    variables.AaAfterHoursGreetingAudioFileId,
                    variables.AaAfterHoursGreetingTextToSpeechPrompt,
                    variables.AaAfterHoursAction,
                    variables.AaAfterHoursActionTarget,
                    variables.AaAfterHoursDisconnectAction),
                new CallQueueTemplate(
                    variables.CqGreetingType,
                    variables.CqGreetingAudioFileId,
                    variables.CqGreetingTextToSpeechPrompt,
                    variables.CqMusicOnHoldType,
                    variables.CqMusicOnHoldAudioFileId,
                    variables.CqOverflowThreshold,
                    variables.CqOverflowAction,
                    variables.CqOverflowActionTarget,
                    variables.CqOverflowVoicemailGreetingType,
                    variables.CqOverflowActionAudioFileId,
                    variables.CqOverflowActionTextToSpeechPrompt,
                    variables.CqOverflowDisconnectAction,
                    variables.CqTimeoutThreshold,
                    variables.CqTimeoutAction,
                    variables.CqTimeoutActionTarget,
                    variables.CqTimeoutVoicemailGreetingType,
                    variables.CqTimeoutActionAudioFileId,
                    variables.CqTimeoutActionTextToSpeechPrompt,
                    variables.CqTimeoutDisconnectAction,
                    variables.CqNoAgentAction,
                    variables.CqNoAgentActionTarget,
                    variables.CqNoAgentVoicemailGreetingType,
                    variables.CqNoAgentActionAudioFileId,
                    variables.CqNoAgentActionTextToSpeechPrompt,
                    variables.CqNoAgentDisconnectAction,
                    variables.CqNoAgentApplyToNewCallsOnly),
                new BusinessHoursTemplate(
                    TimeOnly.FromTimeSpan(variables.OpeningHours1Start),
                    TimeOnly.FromTimeSpan(variables.OpeningHours1End),
                    TimeOnly.FromTimeSpan(variables.OpeningHours2Start),
                    TimeOnly.FromTimeSpan(variables.OpeningHours2End),
                    variables.UsePerDaySchedule,
                    variables.WeeklySchedule.Select(day => new PortableDaySchedule(
                        day.DayName,
                        day.IsEnabled,
                        TimeOnly.FromTimeSpan(day.Hours1Start),
                        TimeOnly.FromTimeSpan(day.Hours1End),
                        TimeOnly.FromTimeSpan(day.Hours2Start),
                        TimeOnly.FromTimeSpan(day.Hours2End),
                        day.HasSecondRange)).ToArray()),
                new HolidayTemplate(
                    variables.HolidayNameSuffix,
                    variables.HolidayGreetingPromptDE,
                    DateOnly.FromDateTime(variables.HolidayDate),
                    TimeOnly.FromTimeSpan(variables.HolidayTime),
                    variables.HolidaySeries.Select(entry => new PortableHolidayEntry(
                        DateOnly.FromDateTime(entry.Date),
                        TimeOnly.FromTimeSpan(entry.Time),
                        entry.Name,
                        entry.EndDate.HasValue ? DateOnly.FromDateTime(entry.EndDate.Value) : null,
                        entry.EndTime.HasValue ? TimeOnly.FromTimeSpan(entry.EndTime.Value) : null)).ToArray())));
    }

    public static PhoneManagerVariables FromDocument(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var configuration = document.Configuration
            ?? throw new InvalidDataException("The configuration payload is missing.");
        var general = configuration.General;
        var autoAttendant = configuration.AutoAttendantTemplate;
        var callQueue = configuration.CallQueueTemplate;
        var hours = configuration.BusinessHoursTemplate;
        var holiday = configuration.HolidayTemplate;

        return new PhoneManagerVariables
        {
            GroupName = general.GroupName,
            GroupDescription = general.GroupDescription,
            Customer = general.Customer,
            CustomerGroupName = general.CustomerGroupName,
            MsFallbackDomain = general.MsFallbackDomain,
            RaaAnrName = general.RaaAnrName,
            CustomerLegalName = general.CustomerLegalName,
            LanguageId = general.LanguageId,
            TimeZoneId = general.TimeZoneId,
            UsageLocation = general.UsageLocation,
            SkuId = general.SkuId,
            CsAppCqId = general.CsAppCqId,
            CsAppAaId = general.CsAppAaId,
            RaaAnr = general.RaaAnr,
            PhoneNumberType = general.PhoneNumberType,
            M365GroupId = general.M365GroupId,
            AaDefaultGreetingType = autoAttendant.DefaultGreetingType,
            AaDefaultGreetingAudioFileId = autoAttendant.DefaultGreetingAudioFileId,
            AaDefaultGreetingTextToSpeechPrompt = autoAttendant.DefaultGreetingTextToSpeechPrompt,
            AaDefaultAction = autoAttendant.DefaultAction,
            AaDefaultActionTarget = autoAttendant.DefaultActionTarget,
            AaDefaultDisconnectAction = autoAttendant.DefaultDisconnectAction,
            AaAfterHoursGreetingType = autoAttendant.AfterHoursGreetingType,
            AaAfterHoursGreetingAudioFileId = autoAttendant.AfterHoursGreetingAudioFileId,
            AaAfterHoursGreetingTextToSpeechPrompt = autoAttendant.AfterHoursGreetingTextToSpeechPrompt,
            AaAfterHoursAction = autoAttendant.AfterHoursAction,
            AaAfterHoursActionTarget = autoAttendant.AfterHoursActionTarget,
            AaAfterHoursDisconnectAction = autoAttendant.AfterHoursDisconnectAction,
            OpeningHours1Start = hours.OpeningHours1Start.ToTimeSpan(),
            OpeningHours1End = hours.OpeningHours1End.ToTimeSpan(),
            OpeningHours2Start = hours.OpeningHours2Start.ToTimeSpan(),
            OpeningHours2End = hours.OpeningHours2End.ToTimeSpan(),
            UsePerDaySchedule = hours.UsePerDaySchedule,
            WeeklySchedule = new ObservableCollection<DaySchedule>(hours.WeeklySchedule.Select(day => new DaySchedule(day.DayName, day.IsEnabled)
            {
                Hours1Start = day.Hours1Start.ToTimeSpan(),
                Hours1End = day.Hours1End.ToTimeSpan(),
                Hours2Start = day.Hours2Start.ToTimeSpan(),
                Hours2End = day.Hours2End.ToTimeSpan(),
                HasSecondRange = day.HasSecondRange,
            })),
            HolidayNameSuffix = holiday.NameSuffix,
            HolidayGreetingPromptDE = holiday.GreetingPromptDe,
            HolidayDate = holiday.DefaultDate.ToDateTime(TimeOnly.MinValue),
            HolidayTime = holiday.DefaultTime.ToTimeSpan(),
            HolidaySeries = new ObservableCollection<HolidayEntry>(holiday.Series.Select(entry => new HolidayEntry(
                entry.Date.ToDateTime(TimeOnly.MinValue),
                entry.Time.ToTimeSpan(),
                entry.Name,
                entry.EndDate?.ToDateTime(TimeOnly.MinValue),
                entry.EndTime?.ToTimeSpan()))),
            CqGreetingType = callQueue.GreetingType,
            CqGreetingAudioFileId = callQueue.GreetingAudioFileId,
            CqGreetingTextToSpeechPrompt = callQueue.GreetingTextToSpeechPrompt,
            CqMusicOnHoldType = callQueue.MusicOnHoldType,
            CqMusicOnHoldAudioFileId = callQueue.MusicOnHoldAudioFileId,
            CqOverflowThreshold = callQueue.OverflowThreshold,
            CqOverflowAction = callQueue.OverflowAction,
            CqOverflowActionTarget = callQueue.OverflowActionTarget,
            CqOverflowVoicemailGreetingType = callQueue.OverflowVoicemailGreetingType,
            CqOverflowActionAudioFileId = callQueue.OverflowActionAudioFileId,
            CqOverflowActionTextToSpeechPrompt = callQueue.OverflowActionTextToSpeechPrompt,
            CqOverflowDisconnectAction = callQueue.OverflowDisconnectAction,
            CqTimeoutThreshold = callQueue.TimeoutThreshold,
            CqTimeoutAction = callQueue.TimeoutAction,
            CqTimeoutActionTarget = callQueue.TimeoutActionTarget,
            CqTimeoutVoicemailGreetingType = callQueue.TimeoutVoicemailGreetingType,
            CqTimeoutActionAudioFileId = callQueue.TimeoutActionAudioFileId,
            CqTimeoutActionTextToSpeechPrompt = callQueue.TimeoutActionTextToSpeechPrompt,
            CqTimeoutDisconnectAction = callQueue.TimeoutDisconnectAction,
            CqNoAgentAction = callQueue.NoAgentAction,
            CqNoAgentActionTarget = callQueue.NoAgentActionTarget,
            CqNoAgentVoicemailGreetingType = callQueue.NoAgentVoicemailGreetingType,
            CqNoAgentActionAudioFileId = callQueue.NoAgentActionAudioFileId,
            CqNoAgentActionTextToSpeechPrompt = callQueue.NoAgentActionTextToSpeechPrompt,
            CqNoAgentDisconnectAction = callQueue.NoAgentDisconnectAction,
            CqNoAgentApplyToNewCallsOnly = callQueue.NoAgentApplyToNewCallsOnly,
        };
    }
}
