using PhoneDesk.Models;
using PhoneDesk.Portability;
using PhoneDesk.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PhoneDesk.Operations
{
    /// <summary>
    /// A configuration value that takes one of a fixed set of texts. <see cref="Closed"/> is true
    /// when no other text is valid: the script builders compare these texts exactly and fall back
    /// to another behaviour (hang up, no greeting) for anything else. When false the list is a
    /// set of known values only, as in the desktop app, where the field also takes free text.
    /// </summary>
    public sealed record ConfigurationOption(string Path, IReadOnlyList<string> Values, bool Closed, string Meaning);

    /// <summary>
    /// Checks of a configuration that the desktop app enforces in its controls (dropdowns, minimum
    /// values, fields that only show for one choice). A head without those controls, such as the MCP
    /// server, calls these checks instead, so a wrong value is reported and does not reach a script.
    /// </summary>
    public static class ConfigurationChecks
    {
        public const string Disconnect = "Disconnect";
        public const string TransferToTarget = "TransferToTarget";
        public const string TransferToVoicemail = "TransferToVoicemail";
        public const string AudioFile = "AudioFile";
        public const string TextToSpeech = "TextToSpeech";

        public static readonly IReadOnlyList<string> GreetingTypes = new[] { "None", AudioFile, TextToSpeech };
        public static readonly IReadOnlyList<string> MusicOnHoldTypes = new[] { "Default", AudioFile };
        public static readonly IReadOnlyList<string> QueueActions = new[] { Disconnect, TransferToTarget, TransferToVoicemail };
        public static readonly IReadOnlyList<string> NoAgentActions = new[] { "QueueCall", Disconnect, TransferToTarget, TransferToVoicemail };
        public static readonly IReadOnlyList<string> VoicemailGreetingTypes = new[] { AudioFile, TextToSpeech };
        public static readonly IReadOnlyList<string> AutoAttendantActions = new[] { Disconnect, TransferToTarget, TransferToVoicemail };

        public static readonly IReadOnlyList<string> Languages = new[]
        {
            "de-DE", "de-AT", "de-CH",
            "en-US", "en-GB", "en-AU", "en-CA",
            "fr-FR", "fr-CH", "fr-CA",
            "it-IT",
            "es-ES", "es-MX",
            "pt-BR", "pt-PT",
            "nl-NL",
            "ja-JP",
            "zh-CN", "zh-TW",
            "ko-KR",
            "ru-RU",
            "pl-PL",
            "sv-SE",
            "nb-NO",
            "da-DK",
            "fi-FI",
            "cs-CZ",
            "tr-TR",
            "ar-SA",
            "he-IL",
            "th-TH",
            "el-GR",
            "hu-HU",
            "ro-RO",
            "sk-SK",
            "uk-UA"
        };

        public static readonly IReadOnlyList<string> TimeZones = new[]
        {
            "W. Europe Standard Time",
            "Central European Standard Time",
            "Romance Standard Time",
            "Central Europe Standard Time",
            "GMT Standard Time",
            "Eastern Standard Time",
            "Central Standard Time",
            "Mountain Standard Time",
            "Pacific Standard Time",
            "AUS Eastern Standard Time",
            "Tokyo Standard Time",
            "China Standard Time",
            "India Standard Time",
            "Singapore Standard Time",
            "Arabian Standard Time",
            "Russian Standard Time",
            "New Zealand Standard Time",
            "SA Pacific Standard Time",
            "E. South America Standard Time",
            "South Africa Standard Time",
            "Israel Standard Time",
            "Turkey Standard Time",
            "Korea Standard Time",
            "SE Asia Standard Time",
            "FLE Standard Time",
            "UTC"
        };

        public static readonly IReadOnlyList<string> UsageLocations = new[]
        {
            "CH", "DE", "AT",
            "FR", "IT", "NL", "BE", "LU",
            "GB", "IE",
            "US", "CA",
            "AU", "NZ",
            "JP", "SG", "IN", "CN",
            "SE", "NO", "DK", "FI",
            "ES", "PT", "GR",
            "PL", "CZ", "SK", "HU", "RO",
            "TR", "IL", "SA", "AE",
            "BR", "MX", "ZA",
            "KR", "TH", "UA", "RU"
        };

        public static readonly IReadOnlyList<string> PhoneNumberTypes = new[] { "DirectRouting", "CallingPlan", "OperatorConnect" };

        /// <summary>Every configuration value with a list of texts, by its path in the configuration document.</summary>
        public static readonly IReadOnlyList<ConfigurationOption> Options = new[]
        {
            new ConfigurationOption("callQueueTemplate.greetingType", GreetingTypes, true, "Greeting a caller hears when the call reaches the queue."),
            new ConfigurationOption("callQueueTemplate.musicOnHoldType", MusicOnHoldTypes, true, "Music while the caller waits."),
            new ConfigurationOption("callQueueTemplate.overflowAction", QueueActions, true, "What happens when the queue is full."),
            new ConfigurationOption("callQueueTemplate.timeoutAction", QueueActions, true, "What happens when a caller waited too long."),
            new ConfigurationOption("callQueueTemplate.noAgentAction", NoAgentActions, true, "What happens when no agent is signed in."),
            new ConfigurationOption("callQueueTemplate.overflowVoicemailGreetingType", VoicemailGreetingTypes, true, "Greeting of the shared voicemail for the overflow action."),
            new ConfigurationOption("callQueueTemplate.timeoutVoicemailGreetingType", VoicemailGreetingTypes, true, "Greeting of the shared voicemail for the timeout action."),
            new ConfigurationOption("callQueueTemplate.noAgentVoicemailGreetingType", VoicemailGreetingTypes, true, "Greeting of the shared voicemail for the no-agent action."),
            new ConfigurationOption("autoAttendantTemplate.defaultGreetingType", GreetingTypes, true, "Greeting during opening hours."),
            new ConfigurationOption("autoAttendantTemplate.defaultAction", AutoAttendantActions, true, "What the auto attendant does during opening hours."),
            new ConfigurationOption("autoAttendantTemplate.afterHoursGreetingType", GreetingTypes, true, "Greeting outside opening hours."),
            new ConfigurationOption("autoAttendantTemplate.afterHoursAction", AutoAttendantActions, true, "What the auto attendant does outside opening hours."),
            new ConfigurationOption("general.languageId", Languages, false, "Language of the auto attendant and call queue."),
            new ConfigurationOption("general.timeZoneId", TimeZones, false, "Windows time zone ID of the auto attendant."),
            new ConfigurationOption("general.usageLocation", UsageLocations, false, "Two-letter country code (ISO 3166-1) of the resource accounts."),
            new ConfigurationOption("general.phoneNumberType", PhoneNumberTypes, false, "How the phone number reaches Teams."),
        };

        /// <summary>
        /// Values that are not valid at all: a text outside a closed list, a waiting time under the
        /// minimum, a holiday end time without an end date. A change that contains one is refused.
        /// </summary>
        public static IReadOnlyList<string> FindInvalidValues(PortableConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var problems = new List<string>();
            foreach (var option in Options.Where(option => option.Closed))
            {
                var value = Read(configuration, option.Path);
                if (!string.IsNullOrEmpty(value) && !option.Values.Contains(value, StringComparer.Ordinal))
                {
                    problems.Add($"{option.Path} is '{value}'. Valid values (exact spelling): {string.Join(", ", option.Values)}.");
                }
            }

            var queue = configuration.CallQueueTemplate;
            if (queue.TimeoutThreshold is < ConstantsService.CallQueue.MinTimeoutThreshold)
            {
                problems.Add(
                    $"callQueueTemplate.timeoutThreshold is {queue.TimeoutThreshold}. The minimum is {ConstantsService.CallQueue.MinTimeoutThreshold} seconds.");
            }

            foreach (var entry in configuration.HolidayTemplate.Series.Where(entry => entry.EndTime is not null && entry.EndDate is null))
            {
                problems.Add(
                    $"The holiday entry of {entry.Date:yyyy-MM-dd} has an endTime but no endDate. Give both (endDate can be the same day), or neither for a full day.");
            }

            return problems;
        }

        /// <summary>
        /// Choices that are not complete: an action or greeting that needs a second value which is
        /// empty. The script builders then use another behaviour without an error, so provisioning
        /// must not start while one of these is open.
        /// </summary>
        public static IReadOnlyList<string> FindIncompleteChoices(IPhoneManagerVariables variables)
        {
            ArgumentNullException.ThrowIfNull(variables);
            var problems = new List<string>();

            void Target(string path, string? action, string? target)
            {
                if ((action == TransferToTarget || action == TransferToVoicemail) && string.IsNullOrWhiteSpace(target))
                {
                    problems.Add(
                        $"{path} is {action}, but {path}Target is empty. Without a target the call is disconnected. " +
                        (action == TransferToVoicemail
                            ? "Set the target to the object ID of the Microsoft 365 group for the shared voicemail."
                            : "Set the target, or use Disconnect."));
                }
            }

            void Greeting(string path, string? type, string? audioFileId, string? prompt, string audioPath, string promptPath)
            {
                if (type == AudioFile && string.IsNullOrWhiteSpace(audioFileId))
                {
                    problems.Add($"{path} is AudioFile, but {audioPath} is empty. Without it nothing is played.");
                }
                else if (type == TextToSpeech && string.IsNullOrWhiteSpace(prompt))
                {
                    problems.Add($"{path} is TextToSpeech, but {promptPath} is empty. Without it nothing is said.");
                }
            }

            Target("callQueueTemplate.overflowAction", variables.CqOverflowAction, variables.CqOverflowActionTarget);
            Target("callQueueTemplate.timeoutAction", variables.CqTimeoutAction, variables.CqTimeoutActionTarget);
            Target("callQueueTemplate.noAgentAction", variables.CqNoAgentAction, variables.CqNoAgentActionTarget);
            Target("autoAttendantTemplate.defaultAction", variables.AaDefaultAction, variables.AaDefaultActionTarget);
            Target("autoAttendantTemplate.afterHoursAction", variables.AaAfterHoursAction, variables.AaAfterHoursActionTarget);

            Greeting("callQueueTemplate.greetingType", variables.CqGreetingType, variables.CqGreetingAudioFileId,
                variables.CqGreetingTextToSpeechPrompt, "callQueueTemplate.greetingAudioFileId", "callQueueTemplate.greetingTextToSpeechPrompt");
            if (variables.CqMusicOnHoldType == AudioFile && string.IsNullOrWhiteSpace(variables.CqMusicOnHoldAudioFileId))
            {
                problems.Add("callQueueTemplate.musicOnHoldType is AudioFile, but callQueueTemplate.musicOnHoldAudioFileId is empty. Without it the default music plays.");
            }

            return problems;
        }

        /// <summary>
        /// Values outside a list of known values (language, time zone, usage location, phone number
        /// type). They can be right, so they are warnings: the tenant decides when the value is used.
        /// </summary>
        public static IReadOnlyList<string> FindUnknownValues(PortableConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var warnings = new List<string>();
            foreach (var option in Options.Where(option => !option.Closed))
            {
                var value = Read(configuration, option.Path);
                if (!string.IsNullOrWhiteSpace(value) && !option.Values.Contains(value, StringComparer.Ordinal))
                {
                    warnings.Add(
                        $"{option.Path} is '{value}', which is not one of the known values. Check it before a step uses it. Known values: {string.Join(", ", option.Values)}.");
                }
            }

            return warnings;
        }

        private static string? Read(PortableConfiguration configuration, string path)
        {
            var queue = configuration.CallQueueTemplate;
            var attendant = configuration.AutoAttendantTemplate;
            var general = configuration.General;
            return path switch
            {
                "callQueueTemplate.greetingType" => queue.GreetingType,
                "callQueueTemplate.musicOnHoldType" => queue.MusicOnHoldType,
                "callQueueTemplate.overflowAction" => queue.OverflowAction,
                "callQueueTemplate.timeoutAction" => queue.TimeoutAction,
                "callQueueTemplate.noAgentAction" => queue.NoAgentAction,
                "callQueueTemplate.overflowVoicemailGreetingType" => queue.OverflowVoicemailGreetingType,
                "callQueueTemplate.timeoutVoicemailGreetingType" => queue.TimeoutVoicemailGreetingType,
                "callQueueTemplate.noAgentVoicemailGreetingType" => queue.NoAgentVoicemailGreetingType,
                "autoAttendantTemplate.defaultGreetingType" => attendant.DefaultGreetingType,
                "autoAttendantTemplate.defaultAction" => attendant.DefaultAction,
                "autoAttendantTemplate.afterHoursGreetingType" => attendant.AfterHoursGreetingType,
                "autoAttendantTemplate.afterHoursAction" => attendant.AfterHoursAction,
                "general.languageId" => general.LanguageId,
                "general.timeZoneId" => general.TimeZoneId,
                "general.usageLocation" => general.UsageLocation,
                "general.phoneNumberType" => general.PhoneNumberType,
                _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown configuration option path.")
            };
        }
    }
}
