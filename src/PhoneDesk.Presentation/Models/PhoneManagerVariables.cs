using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhoneDesk.Services;

namespace PhoneDesk.Models
{
    public partial class PhoneManagerVariables : ObservableObject, IPhoneManagerVariables
    {
        [ObservableProperty]
        private string _groupName = string.Empty;

        [ObservableProperty]
        private string _groupDescription = string.Empty;

        [ObservableProperty]
        private string _customer = string.Empty;

        [ObservableProperty]
        private string _customerGroupName = string.Empty;

        [ObservableProperty]
        private string _msFallbackDomain = string.Empty;

        [ObservableProperty]
        private string _raaAnrName = string.Empty;

        [ObservableProperty]
        private string _customerLegalName = string.Empty;

        [ObservableProperty]
        private string _languageId = string.Empty;

        [ObservableProperty]
        private string _timeZoneId = string.Empty;

        [ObservableProperty]
        private string _usageLocation = string.Empty;

        [ObservableProperty]
        private string _skuId = ConstantsService.TeamsPhone.SkuId;

        [ObservableProperty]
        private string _csAppCqId = ConstantsService.TeamsPhone.CallQueueAppId;

        [ObservableProperty]
        private string _csAppAaId = ConstantsService.TeamsPhone.AutoAttendantAppId;

        [ObservableProperty]
        private string _raaAnr = string.Empty;

        [ObservableProperty]
        private string _phoneNumberType = string.Empty;

        // Auto Attendant Configuration Properties
        // Default Call Flow
        [ObservableProperty]
        private string? _aaDefaultGreetingType; // "None", "AudioFile", "TextToSpeech"

        [ObservableProperty]
        private string? _aaDefaultGreetingAudioFileId;

        [ObservableProperty]
        private string? _aaDefaultGreetingTextToSpeechPrompt;

        [ObservableProperty]
        private string? _aaDefaultAction; // "Disconnect", "TransferToTarget", "TransferToVoicemail"

        [ObservableProperty]
        private string? _aaDefaultActionTarget;

        [ObservableProperty]
        private string? _aaDefaultDisconnectAction; // "None", "AudioFile", "TextToSpeech" - for UI only

        // After Hours Call Flow
        [ObservableProperty]
        private string? _aaAfterHoursGreetingType; // "None", "AudioFile", "TextToSpeech"

        [ObservableProperty]
        private string? _aaAfterHoursGreetingAudioFileId;

        [ObservableProperty]
        private string? _aaAfterHoursGreetingTextToSpeechPrompt;

        [ObservableProperty]
        private string? _aaAfterHoursAction; // "Disconnect", "TransferToTarget", "TransferToVoicemail"

        [ObservableProperty]
        private string? _aaAfterHoursActionTarget;

        [ObservableProperty]
        private string? _aaAfterHoursDisconnectAction; // "None", "AudioFile", "TextToSpeech" - for UI only

        // Teams rejects an empty 00:00–00:00 range, so the first range starts as regular office hours.
        // The second range stays 00:00–00:00, which means "not used".
        [ObservableProperty]
        private TimeSpan _openingHours1Start = new TimeSpan(8, 0, 0);

        [ObservableProperty]
        private TimeSpan _openingHours1End = new TimeSpan(17, 0, 0);

        [ObservableProperty]
        private TimeSpan _openingHours2Start = new TimeSpan(0, 0, 0);

        [ObservableProperty]
        private TimeSpan _openingHours2End = new TimeSpan(0, 0, 0);

        /// <summary>
        /// When true, per-day business hours are used instead of uniform hours.
        /// </summary>
        [ObservableProperty]
        private bool _usePerDaySchedule = false;

        /// <summary>
        /// Per-day business hours configuration. Each day can have its own schedule.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<DaySchedule> _weeklySchedule = new ObservableCollection<DaySchedule>
        {
            new DaySchedule("Monday"),
            new DaySchedule("Tuesday"),
            new DaySchedule("Wednesday"),
            new DaySchedule("Thursday"),
            new DaySchedule("Friday"),
            new DaySchedule("Saturday", isEnabled: false),
            new DaySchedule("Sunday", isEnabled: false)
        };

        [ObservableProperty]
        private string _holidayNameSuffix = string.Empty;

        [ObservableProperty]
        private string _m365GroupId = string.Empty;

        [ObservableProperty]
        private string _holidayGreetingPromptDE = string.Empty;

        [ObservableProperty]
        private DateTime _holidayDate = DateTime.Now;

        [ObservableProperty]
        private TimeSpan _holidayTime = new TimeSpan(9, 0, 0); // Default to 9:00 AM

        [ObservableProperty]
        private ObservableCollection<HolidayEntry> _holidaySeries = new ObservableCollection<HolidayEntry>();

        // Call Queue Configuration Properties
        [ObservableProperty]
        private string? _cqGreetingType; // "None", "AudioFile", "TextToSpeech"

        [ObservableProperty]
        private string? _cqGreetingAudioFileId;

        [ObservableProperty]
        private string? _cqGreetingTextToSpeechPrompt;

        [ObservableProperty]
        private string? _cqMusicOnHoldType; // "Default", "AudioFile"

        [ObservableProperty]
        private string? _cqMusicOnHoldAudioFileId;

        [ObservableProperty]
        private int? _cqOverflowThreshold;

        [ObservableProperty]
        private string? _cqOverflowAction; // "Disconnect", "TransferToTarget", "TransferToVoicemail"

        [ObservableProperty]
        private string? _cqOverflowActionTarget;

        [ObservableProperty]
        private string? _cqOverflowVoicemailGreetingType; // "AudioFile", "TextToSpeech" - for UI only

        [ObservableProperty]
        private string? _cqOverflowActionAudioFileId;

        [ObservableProperty]
        private string? _cqOverflowActionTextToSpeechPrompt;

        [ObservableProperty]
        private string? _cqOverflowDisconnectAction; // "None", "AudioFile", "TextToSpeech" - for UI only, determines what to show

        [ObservableProperty]
        private int? _cqTimeoutThreshold;

        [ObservableProperty]
        private string? _cqTimeoutAction; // "Disconnect", "TransferToTarget", "TransferToVoicemail"

        [ObservableProperty]
        private string? _cqTimeoutActionTarget;

        [ObservableProperty]
        private string? _cqTimeoutVoicemailGreetingType; // "AudioFile", "TextToSpeech" - for UI only

        [ObservableProperty]
        private string? _cqTimeoutActionAudioFileId;

        [ObservableProperty]
        private string? _cqTimeoutActionTextToSpeechPrompt;

        [ObservableProperty]
        private string? _cqTimeoutDisconnectAction; // "None", "AudioFile", "TextToSpeech" - for UI only, determines what to show

        [ObservableProperty]
        private string? _cqNoAgentAction; // "QueueCall", "Disconnect", "TransferToTarget", "TransferToVoicemail"

        [ObservableProperty]
        private string? _cqNoAgentActionTarget;

        [ObservableProperty]
        private string? _cqNoAgentVoicemailGreetingType; // "AudioFile", "TextToSpeech" - for UI only

        [ObservableProperty]
        private string? _cqNoAgentActionAudioFileId;

        [ObservableProperty]
        private string? _cqNoAgentActionTextToSpeechPrompt;

        [ObservableProperty]
        private string? _cqNoAgentDisconnectAction; // "None", "AudioFile", "TextToSpeech" - for UI only, determines what to show

        [ObservableProperty]
        private bool _cqNoAgentApplyToNewCallsOnly;

        public string M365Group => ResourceNames.M365Group(Customer, CustomerGroupName);
        public string RacqUPN => ResourceAccountUpn.Build(RacqDisplayName, MsFallbackDomain);
        public string RacqDisplayName => ResourceNames.CallQueueResourceAccount(Customer, CustomerGroupName);
        public string CqDisplayName => ResourceNames.CallQueue(Customer, CustomerGroupName);
        public string RaaaUPN => ResourceAccountUpn.Build(RaaaDisplayName, MsFallbackDomain);
        public string RaaaDisplayName => ResourceNames.AutoAttendantResourceAccount(Customer, RaaAnrName, CustomerGroupName);
        public string AaDisplayName => ResourceNames.AutoAttendant(Customer, RaaAnrName, CustomerGroupName);

        // Explicit IPhoneManagerVariables member: expose the bindable ObservableCollection as the
        // read-only Domain contract (covariant ObservableCollection<DaySchedule> -> IReadOnlyList<IDaySchedule>).
        IReadOnlyList<IDaySchedule> IPhoneManagerVariables.WeeklySchedule => WeeklySchedule;

        public string HolidayName
        {
            get => ResourceNames.Holiday(Customer, HolidayNameSuffix);
            set
            {
                string prefix = $"hd-{Customer}-";
                string newValue = value;

                while (newValue.StartsWith("hd-"))
                {
                    int nextDash = newValue.IndexOf('-', 3);
                    if (nextDash == -1) break;
                    
                    string potentialCustomer = newValue.Substring(3, nextDash - 3);
                    if (potentialCustomer == Customer)
                    {
                        newValue = newValue.Substring(nextDash + 1);
                    }
                    else break;
                }

                HolidayNameSuffix = newValue;
                OnPropertyChanged();
            }
        }

        partial void OnCustomerChanged(string value)
        {
            OnPropertyChanged(nameof(M365Group));
            OnPropertyChanged(nameof(RacqUPN));
            OnPropertyChanged(nameof(RacqDisplayName));
            OnPropertyChanged(nameof(CqDisplayName));
            OnPropertyChanged(nameof(RaaaUPN));
            OnPropertyChanged(nameof(RaaaDisplayName));
            OnPropertyChanged(nameof(AaDisplayName));
            OnPropertyChanged(nameof(HolidayName));
        }

        partial void OnCustomerGroupNameChanged(string value)
        {
            OnPropertyChanged(nameof(M365Group));
            OnPropertyChanged(nameof(RacqUPN));
            OnPropertyChanged(nameof(RacqDisplayName));
            OnPropertyChanged(nameof(CqDisplayName));
            OnPropertyChanged(nameof(RaaaUPN));
            OnPropertyChanged(nameof(RaaaDisplayName));
            OnPropertyChanged(nameof(AaDisplayName));
        }

        partial void OnMsFallbackDomainChanged(string value)
        {
            OnPropertyChanged(nameof(RacqUPN));
            OnPropertyChanged(nameof(RaaaUPN));
        }

        partial void OnRaaAnrNameChanged(string value)
        {
            OnPropertyChanged(nameof(RaaaUPN));
            OnPropertyChanged(nameof(RaaaDisplayName));
            OnPropertyChanged(nameof(AaDisplayName));
        }

        partial void OnHolidayNameSuffixChanged(string value)
        {
            OnPropertyChanged(nameof(HolidayName));
        }

        partial void OnOpeningHours1StartChanged(TimeSpan value)
        {
            OnPropertyChanged();
        }

        partial void OnOpeningHours1EndChanged(TimeSpan value)
        {
            OnPropertyChanged();
        }

        partial void OnOpeningHours2StartChanged(TimeSpan value)
        {
            OnPropertyChanged();
        }

        partial void OnOpeningHours2EndChanged(TimeSpan value)
        {
            OnPropertyChanged();
        }

        partial void OnM365GroupIdChanged(string value)
        {
            // Prefill target fields if they are empty
            if (string.IsNullOrWhiteSpace(CqOverflowActionTarget) && !string.IsNullOrWhiteSpace(value))
            {
                CqOverflowActionTarget = value;
            }
            if (string.IsNullOrWhiteSpace(CqTimeoutActionTarget) && !string.IsNullOrWhiteSpace(value))
            {
                CqTimeoutActionTarget = value;
            }
            if (string.IsNullOrWhiteSpace(CqNoAgentActionTarget) && !string.IsNullOrWhiteSpace(value))
            {
                CqNoAgentActionTarget = value;
            }
        }
    }
} 
