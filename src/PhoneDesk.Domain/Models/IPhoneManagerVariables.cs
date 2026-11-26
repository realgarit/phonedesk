using System.Collections.Generic;

namespace PhoneDesk.Models
{
    /// <summary>
    /// Read-only Domain contract exposing the configuration the framework-free layers
    /// (validation, script builders) consume. The Presentation <c>PhoneManagerVariables</c>
    /// (an ObservableObject bound to the UI) implements this, so the inner layers depend on this
    /// Domain abstraction rather than on the concrete MVVM model. Members are getters only — the
    /// inner layers never mutate the configuration.
    /// </summary>
    public interface IPhoneManagerVariables
    {
        // Core / customer
        string Customer { get; }
        string CustomerGroupName { get; }
        string CustomerLegalName { get; }
        string MsFallbackDomain { get; }
        string LanguageId { get; }
        string TimeZoneId { get; }
        string UsageLocation { get; }
        string RaaAnr { get; }
        string PhoneNumberType { get; }
        string SkuId { get; }
        string CsAppCqId { get; }
        string CsAppAaId { get; }

        // Computed naming / UPNs
        string M365Group { get; }
        string RacqUPN { get; }
        string RacqDisplayName { get; }
        string CqDisplayName { get; }
        string RaaaUPN { get; }
        string RaaaDisplayName { get; }
        string AaDisplayName { get; }
        string HolidayName { get; }

        // Holiday
        string HolidayNameSuffix { get; }
        string HolidayGreetingPromptDE { get; }

        // Auto Attendant — default call flow
        string? AaDefaultGreetingType { get; }
        string? AaDefaultGreetingAudioFileId { get; }
        string? AaDefaultGreetingTextToSpeechPrompt { get; }
        string? AaDefaultAction { get; }
        string? AaDefaultActionTarget { get; }

        // Auto Attendant — after hours call flow
        string? AaAfterHoursGreetingType { get; }
        string? AaAfterHoursGreetingAudioFileId { get; }
        string? AaAfterHoursGreetingTextToSpeechPrompt { get; }
        string? AaAfterHoursAction { get; }
        string? AaAfterHoursActionTarget { get; }

        // Business hours
        TimeSpan OpeningHours1Start { get; }
        TimeSpan OpeningHours1End { get; }
        TimeSpan OpeningHours2Start { get; }
        TimeSpan OpeningHours2End { get; }
        bool UsePerDaySchedule { get; }
        IReadOnlyList<IDaySchedule> WeeklySchedule { get; }

        // Call Queue — greeting / music on hold
        string? CqGreetingType { get; }
        string? CqGreetingAudioFileId { get; }
        string? CqGreetingTextToSpeechPrompt { get; }
        string? CqMusicOnHoldType { get; }
        string? CqMusicOnHoldAudioFileId { get; }

        // Call Queue — overflow
        int? CqOverflowThreshold { get; }
        string? CqOverflowAction { get; }
        string? CqOverflowActionTarget { get; }
        string? CqOverflowVoicemailGreetingType { get; }
        string? CqOverflowActionAudioFileId { get; }
        string? CqOverflowActionTextToSpeechPrompt { get; }

        // Call Queue — timeout
        int? CqTimeoutThreshold { get; }
        string? CqTimeoutAction { get; }
        string? CqTimeoutActionTarget { get; }
        string? CqTimeoutVoicemailGreetingType { get; }
        string? CqTimeoutActionAudioFileId { get; }
        string? CqTimeoutActionTextToSpeechPrompt { get; }

        // Call Queue — no agent
        string? CqNoAgentAction { get; }
        string? CqNoAgentActionTarget { get; }
        string? CqNoAgentVoicemailGreetingType { get; }
        string? CqNoAgentActionAudioFileId { get; }
        string? CqNoAgentActionTextToSpeechPrompt { get; }
        bool CqNoAgentApplyToNewCallsOnly { get; }

        // Call Queue — outbound caller ID
        /// <summary>
        /// True when agents of the call queue can call out with the number of the auto attendant:
        /// the auto attendant resource account is set as caller ID resource account of the call queue.
        /// </summary>
        bool CqUseAutoAttendantNumberAsCallerId { get; }
    }
}
