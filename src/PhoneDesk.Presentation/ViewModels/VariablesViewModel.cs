using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using PhoneDesk.Models;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using PhoneDesk.Helpers;
using PhoneDesk.Localization;
using System.Collections.Generic;

namespace PhoneDesk.ViewModels
{
    public partial class VariablesViewModel : ViewModelBase
    {
        private PhoneManagerVariables? _subscribedVariables;

        // Removed TeamsConnected and GraphConnected - no longer needed since lock was removed

        [ObservableProperty]
        private bool _showHolidayTimePicker = false;

        /// <summary>
        /// Observable collection of time options in 15-minute increments (00:00 to 23:45).
        /// Uses the shared TimeOptionsProvider for consistency across the application.
        /// </summary>
        public ObservableCollection<TimeSpan> TimeOptions => TimeOptionsProvider.TimeOptions;

        [ObservableProperty]
        private TimeSpan? _selectedHolidayTime;

        [ObservableProperty]
        private bool _showHolidaySeriesManager = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DialogHolidayDate))]
        private HolidayEntry? _editingHoliday;

        [ObservableProperty]
        private bool _showEditHolidayDialog = false;

        [ObservableProperty]
        private TimeSpan? _selectedEditHolidayTime;

        [ObservableProperty]
        private bool _dialogHasEndDate;

        [ObservableProperty]
        private DateTime _dialogEndDate = DateTime.Now;

        [ObservableProperty]
        private TimeSpan? _selectedEditEndTime;

        // Simple properties for Add/Edit dialogs
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DialogHolidayDate))]
        private DateTime _newHolidayDate = DateTime.Now;

        [ObservableProperty]
        private TimeSpan _newHolidayTime = new TimeSpan(0, 0, 0);

        [ObservableProperty]
        private TimeSpan _editHolidayTime = new TimeSpan(0, 0, 0);

        // Computed properties for the dialog
        public DateTime? DialogHolidayDate
        {
            get => EditingHoliday?.Date ?? NewHolidayDate;
            set
            {
                if (value.HasValue)
                {
                    if (EditingHoliday != null)
                        EditingHoliday.Date = value.Value;
                    else
                        NewHolidayDate = value.Value;
                }
            }
        }

        // Removed DialogHolidayTime - now using SelectedEditHolidayTime with ComboBoxItem approach

        /// <summary>
        /// Teams-supported language IDs for auto attendants and call queues.
        /// </summary>
        public static ObservableCollection<string> LanguageOptions { get; } = new ObservableCollection<string>
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

        /// <summary>
        /// Common Windows time zone IDs used with Teams.
        /// </summary>
        public static ObservableCollection<string> TimeZoneOptions { get; } = new ObservableCollection<string>
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

        /// <summary>
        /// ISO 3166-1 alpha-2 country codes for usage location.
        /// </summary>
        public static ObservableCollection<string> UsageLocationOptions { get; } = new ObservableCollection<string>
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

        /// <summary>
        /// Phone number assignment types for Teams.
        /// </summary>
        public static ObservableCollection<string> PhoneNumberTypeOptions { get; } = new ObservableCollection<string>
        {
            "DirectRouting",
            "CallingPlan",
            "OperatorConnect"
        };

        public VariablesViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            LogLocalized(UiTextKey.VariablesPageLoadedLog, "Variables page loaded", LogLevel.Info);

            // Subscribe to variable changes for Call Queue configuration visibility
            if (_sharedStateService?.Variables != null)
            {
                SubscribeToVariablesChanges(_sharedStateService.Variables);

                // Prefill target fields if M365GroupId is already set
                PrefillCallQueueTargets();
            }
        }

        private void SubscribeToVariablesChanges(PhoneManagerVariables variables)
        {
            // Unsubscribe from previous instance if any
            if (_subscribedVariables != null)
            {
                _subscribedVariables.PropertyChanged -= OnVariablesPropertyChanged;
            }

            _subscribedVariables = variables;
            variables.PropertyChanged += OnVariablesPropertyChanged;
        }

        private void OnVariablesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName?.StartsWith("Cq") == true)
            {
                UpdateCallQueueVisibility();
            }
            else if (e.PropertyName == nameof(PhoneManagerVariables.M365GroupId))
            {
                PrefillCallQueueTargets();
            }
            else if (e.PropertyName?.StartsWith("Aa") == true)
            {
                UpdateAutoAttendantVisibility();
            }
        }

        private void PrefillCallQueueTargets()
        {
            var variables = Variables;
            if (variables == null) return;

            var groupId = variables.M365GroupId;
            if (string.IsNullOrWhiteSpace(groupId)) return;

            // Prefill target fields if they are empty
            if (string.IsNullOrWhiteSpace(variables.CqOverflowActionTarget))
            {
                variables.CqOverflowActionTarget = groupId;
            }
            if (string.IsNullOrWhiteSpace(variables.CqTimeoutActionTarget))
            {
                variables.CqTimeoutActionTarget = groupId;
            }
            if (string.IsNullOrWhiteSpace(variables.CqNoAgentActionTarget))
            {
                variables.CqNoAgentActionTarget = groupId;
            }
        }

        public PhoneManagerVariables Variables
        {
            get => _sharedStateService?.Variables ?? new PhoneManagerVariables();
            set
            {
                if (_sharedStateService != null)
                {
                    _sharedStateService.Variables = value;
                    OnPropertyChanged();
                    
                    // Subscribe to changes on the new Variables instance
                    if (value != null)
                    {
                        SubscribeToVariablesChanges(value);
                        
                        // Prefill target fields if M365GroupId is already set
                        PrefillCallQueueTargets();
                    }
                }
            }
        }

        public bool CanProceed => true; // Removed lock - Variables page is always accessible

        private string GetAudioContextText(string context) => context switch
        {
            "Greeting" => GetText(UiTextKey.VariablesAudioGreetingContext, "Greeting"),
            "Music on Hold" => GetText(UiTextKey.VariablesAudioMusicOnHoldContext, "Music on hold"),
            "Overflow Voicemail Greeting" => GetText(UiTextKey.VariablesAudioOverflowVoicemailContext, "Overflow voicemail greeting"),
            "Timeout Voicemail Greeting" => GetText(UiTextKey.VariablesAudioTimeoutVoicemailContext, "Timeout voicemail greeting"),
            "No Agent Voicemail Greeting" => GetText(UiTextKey.VariablesAudioNoAgentVoicemailContext, "No-agent voicemail greeting"),
            "AA Default Greeting" => GetText(UiTextKey.VariablesAudioDefaultGreetingContext, "Default greeting"),
            "AA After Hours Greeting" => GetText(UiTextKey.VariablesAudioAfterHoursGreetingContext, "After-hours greeting"),
            _ => context
        };

        [RelayCommand]
        private async Task SaveVariablesToFileAsync()
        {
            try
            {
                var downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                downloadsPath = Path.Combine(downloadsPath, "Downloads");
                
                var fileName = $"PhoneManagerVariables_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                var filePath = Path.Combine(downloadsPath, fileName);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(Variables, jsonOptions);
                await File.WriteAllTextAsync(filePath, json);

                LogLocalized(
                    UiTextKey.VariablesSavedLog,
                    "Configuration saved to: {path}",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["path"] = filePath });
                await _errorHandlingService.ShowSuccess(
                    GetText(
                        UiTextKey.VariablesSaveSuccessMessage,
                        "Configuration saved successfully to:\n{path}",
                        new Dictionary<string, object?> { ["path"] = filePath }),
                    GetText(UiTextKey.VariablesSaveSuccessTitle, "Save successful"));
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesSaveFailedLog,
                    "Error saving configuration: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                await _errorHandlingService.HandleGenericError(
                    GetText(
                        UiTextKey.VariablesSaveFailedMessage,
                        "Error saving configuration:\n{error}",
                        new Dictionary<string, object?> { ["error"] = ex.Message }),
                    "SaveVariables");
            }
        }

        [RelayCommand]
        private async Task LoadVariablesFromFileAsync()
        {
            try
            {
                var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                if (window?.MainWindow != null)
                {
                    var storageProvider = window.MainWindow.StorageProvider;
                    var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    var suggestedLocation = await storageProvider.TryGetFolderFromPathAsync(new Uri(downloadsPath));
                    
                    var file = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = GetText(UiTextKey.VariablesLoadPickerTitle, "Load configuration from file"),
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType(GetText(UiTextKey.VariablesJsonFiles, "JSON files")) { Patterns = new[] { "*.json" } },
                            new Avalonia.Platform.Storage.FilePickerFileType(GetText(UiTextKey.VariablesAllFiles, "All files")) { Patterns = new[] { "*" } }
                        },
                        SuggestedStartLocation = suggestedLocation
                    });

                    if (file != null && file.Count > 0)
                    {
                        var fileName = file[0].Path.LocalPath;
                        var json = await File.ReadAllTextAsync(fileName);
                        var jsonOptions = new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        };

                        var loadedVariables = JsonSerializer.Deserialize<PhoneManagerVariables>(json, jsonOptions);
                        
                        if (loadedVariables != null)
                        {
                            Variables = loadedVariables;
                            LogLocalized(
                                UiTextKey.VariablesLoadedLog,
                                "Configuration loaded from: {path}",
                                LogLevel.Info,
                                new Dictionary<string, object?> { ["path"] = fileName });
                            await _errorHandlingService.ShowSuccess(
                                GetText(
                                    UiTextKey.VariablesLoadSuccessMessage,
                                    "Configuration loaded successfully from:\n{path}",
                                    new Dictionary<string, object?> { ["path"] = fileName }),
                                GetText(UiTextKey.VariablesLoadSuccessTitle, "Load successful"));
                        }
                        else
                        {
                            await _errorHandlingService.HandleGenericError(
                                GetText(
                                    UiTextKey.VariablesLoadFailedMessage,
                                    "Failed to load configuration from the selected file."),
                                "LoadVariables");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesLoadFailedLog,
                    "Error loading configuration: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                await _errorHandlingService.HandleGenericError(
                    GetText(
                        UiTextKey.VariablesLoadFailedMessage,
                        "Error loading configuration:\n{error}",
                        new Dictionary<string, object?> { ["error"] = ex.Message }),
                    "LoadVariables");
            }
        }

        [RelayCommand]
        private void OpenHolidayTimePicker()
        {
            // Set the current time as selected if it exists
            var currentTime = Variables.HolidayTime;
            // Round to nearest 15-minute increment if needed
            var roundedTime = new TimeSpan(currentTime.Hours, (currentTime.Minutes / 15) * 15, 0);
            SelectedHolidayTime = roundedTime;
            
            ShowHolidayTimePicker = true;
        }

        [RelayCommand]
        private void CloseHolidayTimePicker()
        {
            ShowHolidayTimePicker = false;
        }

        [RelayCommand]
        private void SaveHolidayTime()
        {
            if (SelectedHolidayTime.HasValue)
            {
                Variables.HolidayTime = SelectedHolidayTime.Value;
                LogLocalized(
                    UiTextKey.VariablesHolidayTimeUpdatedLog,
                    "Holiday time updated to: {time}",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["time"] = SelectedHolidayTime.Value.ToString(@"hh\:mm") });
            }
            
            ShowHolidayTimePicker = false;
        }

        [RelayCommand]
        private void OpenHolidaySeriesManager()
        {
            // Save original state for cancel functionality
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                OriginalHolidaySeries.Clear();
                foreach (var holiday in variables.HolidaySeries)
                {
                    OriginalHolidaySeries.Add(new HolidayEntry(holiday.Date, holiday.Time));
                }
            }
            
            ShowHolidaySeriesManager = true;
        }

        [ObservableProperty]
        private ObservableCollection<HolidayEntry> _originalHolidaySeries = new ObservableCollection<HolidayEntry>();

        [RelayCommand]
        private void CloseHolidaySeriesManager()
        {
            ShowHolidaySeriesManager = false;
            EditingHoliday = null;
        }

        [RelayCommand]
        private void CancelHolidaySeriesManager()
        {
            // Restore original state
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                variables.HolidaySeries.Clear();
                foreach (var holiday in OriginalHolidaySeries)
                {
                    variables.HolidaySeries.Add(holiday);
                }
            }
            
            ShowHolidaySeriesManager = false;
            EditingHoliday = null;
        }

        [RelayCommand]
        private void AddHoliday()
        {
            try
            {
                // Reset to default values
                NewHolidayTime = new TimeSpan(0, 0, 0);
                EditingHoliday = null; // This is a new holiday, not editing existing
                
                // Set the selected time for the dialog (round to nearest 15-minute increment)
                var roundedTime = new TimeSpan(NewHolidayTime.Hours, (NewHolidayTime.Minutes / 15) * 15, 0);
                SelectedEditHolidayTime = roundedTime;
                
                // Open the edit dialog
                ShowEditHolidayDialog = true;
                
                LogLocalized(UiTextKey.VariablesAddHolidayDialogOpenedLog, "Add holiday dialog opened", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesOpenAddHolidayFailedLog,
                    "Error opening add holiday dialog: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        // Predefined sets wizard state
        [ObservableProperty]
        private bool _showPredefinedHolidaysWizard = false;

        [ObservableProperty]
        private bool _showAargauInfoDialog = false;

        [ObservableProperty]
        private ObservableCollection<string> _countries = new ObservableCollection<string>(new[] { "Switzerland" });

        [ObservableProperty]
        private string? _selectedCountry = "Switzerland";

        [ObservableProperty]
        private ObservableCollection<string> _cantons = new ObservableCollection<string>(new[] { 
            "Aargau", "Appenzell-Ausserrhoden", "Appenzell-Innerrhoden", "Basel-Land", "Basel-Stadt",
            "Bern", "Fribourg", "Genf", "Glarus", "Graubünden", "Jura", "Luzern", 
            "Neuenburg", "Nidwalden", "Obwalden", "Schaffhausen", "Schwyz", "Solothurn",
            "St. Gallen", "Tessin", "Thurgau", "Uri", "Waadt", "Wallis", "Zug", "Zürich" 
        });

        [ObservableProperty]
        private string? _selectedCanton;

        [ObservableProperty]
        private ObservableCollection<string> _bezirke = new ObservableCollection<string>(new[] { 
            "Aarau (Brugg/Kulm/Lenzburg/Zofingen/Baden - nur Bergdietikon)", 
            "Baden (ohne Bergdietikon)", 
            "Bremgarten", 
            "Muri (Laufenburg, Rheinfelden - nur Hellikon, Mumpf, Obermumpf, Schupfart, Stein, Wegstetten)", 
            "Rheinfelden (nur Kaiseraugst, Magden, Möhlin, Olsberg, Rheinfelden, Wallbach, Zeiningen, Zuzgen)", 
            "Zurzach" 
        });

        [ObservableProperty]
        private string? _selectedBezirk;

        [ObservableProperty]
        private ObservableCollection<int> _years = new ObservableCollection<int>(Enumerable.Range(Math.Max(2025, DateTime.Now.Year), Math.Max(0, 2030 - Math.Max(2025, DateTime.Now.Year) + 1)));

        [ObservableProperty]
        private int _selectedYear = Math.Max(2025, Math.Min(2030, DateTime.Now.Year));

        public bool IsSwitzerlandSelected => string.Equals(SelectedCountry, "Switzerland", StringComparison.OrdinalIgnoreCase);
        public bool IsAargauSelected => string.Equals(SelectedCanton, "Aargau", StringComparison.OrdinalIgnoreCase);

        partial void OnSelectedCountryChanged(string? value)
        {
            OnPropertyChanged(nameof(IsSwitzerlandSelected));
            OnPropertyChanged(nameof(CanApplyPredefinedSelection));
        }

        partial void OnSelectedCantonChanged(string? value)
        {
            OnPropertyChanged(nameof(IsAargauSelected));
            OnPropertyChanged(nameof(CanApplyPredefinedSelection));
        }

        public bool CanApplyPredefinedSelection
        {
            get
            {
                if (!IsSwitzerlandSelected)
                    return SelectedYear >= 2025 && SelectedYear <= 2030;

                if (IsAargauSelected)
                    return !string.IsNullOrEmpty(SelectedBezirk) && SelectedYear >= 2025 && SelectedYear <= 2030;

                return !string.IsNullOrEmpty(SelectedCanton) && SelectedYear >= 2025 && SelectedYear <= 2030;
            }
        }

        partial void OnSelectedBezirkChanged(string? value)
        {
            OnPropertyChanged(nameof(CanApplyPredefinedSelection));
        }

        partial void OnSelectedYearChanged(int value)
        {
            OnPropertyChanged(nameof(CanApplyPredefinedSelection));
        }

        [RelayCommand]
        private void OpenPredefinedHolidaysWizard()
        {
            try
            {
                SelectedCountry = "Switzerland";
                SelectedCanton = null;
                SelectedBezirk = null;
                SelectedYear = Math.Max(2025, Math.Min(2030, DateTime.Now.Year));
                ShowPredefinedHolidaysWizard = true;
                LogLocalized(UiTextKey.VariablesPredefinedWizardOpenedLog, "Opened predefined holidays wizard", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesOpenPredefinedWizardFailedLog,
                    "Error opening predefined holidays wizard: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void CancelPredefinedHolidaysWizard()
        {
            ShowPredefinedHolidaysWizard = false;
        }

        [RelayCommand]
        private void ShowAargauInfo()
        {
            try
            {
                ShowAargauInfoDialog = true;
                LogLocalized(UiTextKey.VariablesAargauReferenceOpenedLog, "Opened Aargau holiday reference info", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesOpenAargauReferenceFailedLog,
                    "Error opening Aargau holiday reference info: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void CloseAargauInfo()
        {
            ShowAargauInfoDialog = false;
        }

        [RelayCommand]
        private void ApplyPredefinedHolidays()
        {
            try
            {
                var variables = _sharedStateService?.Variables;
                if (variables == null) return;

                // Use Swiss provider for Switzerland; expand later for other countries
                var provider = new PhoneDesk.Services.Holidays.SwissHolidaysProvider();
                var result = provider.GetHolidaysDetailed(SelectedCountry ?? "", SelectedCanton, SelectedBezirk, SelectedYear);

                if (result.Completeness != PhoneDesk.Holidays.HolidayResultCompleteness.Complete)
                {
                    LogLocalized(
                        UiTextKey.VariablesPredefinedHolidayRegionIncompleteLog,
                        "Predefined holidays for '{canton}' are regionally incomplete ({completeness}): only the canton-wide intersection was applied. A region or district is required for the full list.",
                        LogLevel.Warning,
                        new Dictionary<string, object?>
                        {
                            ["canton"] = SelectedCanton,
                            ["completeness"] = result.Completeness
                        });
                }

                foreach (var holiday in result.Holidays)
                {
                    variables.HolidaySeries.Add(holiday);
                    LogLocalized(
                        UiTextKey.VariablesPredefinedHolidayAddedLog,
                        "Added predefined holiday: {holiday}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["holiday"] = holiday.DisplayText });
                }

                ShowPredefinedHolidaysWizard = false;
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesApplyPredefinedHolidaysFailedLog,
                    "Error applying predefined holidays: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }
        [RelayCommand]
        private void EditHoliday(HolidayEntry? holiday)
        {
            try
            {
                if (holiday != null)
                {
                    LogLocalized(
                        UiTextKey.VariablesEditHolidayStartedLog,
                        "Starting holiday edit",
                        LogLevel.Info);
                    EditingHoliday = holiday;
                    
                    // Set the selected time, rounding to nearest 15-minute increment if needed
                    var roundedTime = new TimeSpan(holiday.Time.Hours, (holiday.Time.Minutes / 15) * 15, 0);
                    SelectedEditHolidayTime = roundedTime;

                    // Set end date fields
                    DialogHasEndDate = holiday.HasEndDate;
                    DialogEndDate = holiday.EndDate ?? holiday.Date.AddDays(1);
                    if (holiday.EndTime.HasValue)
                    {
                        var roundedEnd = new TimeSpan(holiday.EndTime.Value.Hours, (holiday.EndTime.Value.Minutes / 15) * 15, 0);
                        SelectedEditEndTime = roundedEnd;
                    }
                    else
                    {
                        SelectedEditEndTime = null;
                    }
                    
                    ShowEditHolidayDialog = true;
                    LogLocalized(
                        UiTextKey.VariablesEditHolidayDialogReadyLog,
                        "Holiday edit dialog is ready",
                        LogLevel.Info);
                    LogLocalized(
                        UiTextKey.VariablesEditHolidayRequestedLog,
                        "Edit holiday requested: {holiday}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["holiday"] = holiday.DisplayText });
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesEditHolidayFailedLog,
                    "Error editing holiday: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void CancelEditHoliday()
        {
            ShowEditHolidayDialog = false;
            EditingHoliday = null;
            SelectedEditHolidayTime = null;
            SelectedEditEndTime = null;
            DialogHasEndDate = false;
        }

        [RelayCommand]
        private void SaveEditHoliday()
        {
            try
            {
                var variables = _sharedStateService?.Variables;
                if (variables == null) return;

                // Get the selected time from the ComboBox
                TimeSpan selectedTime = SelectedEditHolidayTime ?? new TimeSpan(0, 0, 0);

                if (EditingHoliday != null)
                {
                    // Editing existing holiday
                    EditingHoliday.Time = selectedTime;
                    EditingHoliday.EndDate = DialogHasEndDate ? DialogEndDate : null;
                    EditingHoliday.EndTime = DialogHasEndDate ? (SelectedEditEndTime ?? TimeSpan.Zero) : null;
                    LogLocalized(
                        UiTextKey.VariablesHolidayUpdatedLog,
                        "Updated holiday: {holiday}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["holiday"] = EditingHoliday.DisplayText });
                }
                else
                {
                    // Adding new holiday
                    var newHoliday = new HolidayEntry
                    {
                        Date = NewHolidayDate,
                        Time = selectedTime,
                        EndDate = DialogHasEndDate ? DialogEndDate : null,
                        EndTime = DialogHasEndDate ? (SelectedEditEndTime ?? TimeSpan.Zero) : null
                    };
                    variables.HolidaySeries.Add(newHoliday);
                    LogLocalized(
                        UiTextKey.VariablesHolidayAddedLog,
                        "Added holiday: {holiday}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["holiday"] = newHoliday.DisplayText });
                }
                
                ShowEditHolidayDialog = false;
                EditingHoliday = null;
                SelectedEditHolidayTime = null;
                SelectedEditEndTime = null;
                DialogHasEndDate = false;
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesSaveHolidayFailedLog,
                    "Error saving holiday: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void RemoveHoliday(HolidayEntry? holiday)
        {
            try
            {
                if (holiday != null)
                {
                    var variables = _sharedStateService?.Variables;
                    if (variables != null)
                    {
                        variables.HolidaySeries.Remove(holiday);
                        LogLocalized(
                            UiTextKey.VariablesHolidayDeletedLog,
                            "Deleted holiday: {holiday}",
                            LogLevel.Info,
                            new Dictionary<string, object?> { ["holiday"] = holiday.DisplayText });
                    }
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesDeleteHolidayFailedLog,
                    "Error deleting holiday: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void DeleteAllHolidays()
        {
            try
            {
                var variables = _sharedStateService?.Variables;
                if (variables != null && variables.HolidaySeries.Count > 0)
                {
                    var count = variables.HolidaySeries.Count;
                    variables.HolidaySeries.Clear();
                    LogLocalized(
                        UiTextKey.VariablesDeleteAllHolidaysLog,
                        "Deleted all {count} holidays from the series",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["count"] = count });
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesDeleteAllHolidaysFailedLog,
                    "Error deleting all holidays: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void SaveHolidaySeries()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                LogLocalized(
                    UiTextKey.VariablesSaveHolidaySeriesLog,
                    "Saved holiday series with {count} holidays",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["count"] = variables.HolidaySeries.Count });
                ShowHolidaySeriesManager = false;
            }
        }

        // Call Queue Configuration Properties
        public ObservableCollection<string> GreetingTypeOptions { get; } = new ObservableCollection<string>
        {
            "None",
            "AudioFile",
            "TextToSpeech"
        };

        public ObservableCollection<string> MusicOnHoldTypeOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "AudioFile"
        };

        public ObservableCollection<string> OverflowActionOptions { get; } = new ObservableCollection<string>
        {
            "Disconnect",
            "TransferToTarget",
            "TransferToVoicemail"
        };

        public ObservableCollection<string> TimeoutActionOptions { get; } = new ObservableCollection<string>
        {
            "Disconnect",
            "TransferToTarget",
            "TransferToVoicemail"
        };

        public ObservableCollection<string> NoAgentActionOptions { get; } = new ObservableCollection<string>
        {
            "QueueCall",
            "Disconnect",
            "TransferToTarget",
            "TransferToVoicemail"
        };

        public ObservableCollection<string> VoicemailGreetingTypeOptions { get; } = new ObservableCollection<string>
        {
            "AudioFile",
            "TextToSpeech"
        };

        public ObservableCollection<string> AaGreetingTypeOptions { get; } = new ObservableCollection<string>
        {
            "None",
            "AudioFile",
            "TextToSpeech"
        };

        public ObservableCollection<string> AaActionOptions { get; } = new ObservableCollection<string>
        {
            "Disconnect",
            "TransferToTarget",
            "TransferToVoicemail"
        };

        // Conditional visibility properties
        public bool ShowGreetingAudioFile => Variables.CqGreetingType == "AudioFile";
        public bool ShowGreetingTextToSpeech => Variables.CqGreetingType == "TextToSpeech";
        public bool ShowMusicOnHoldAudioFile => Variables.CqMusicOnHoldType == "AudioFile";
        public bool ShowOverflowTarget => Variables.CqOverflowAction == "TransferToTarget" || Variables.CqOverflowAction == "TransferToVoicemail";
        public bool ShowTimeoutTarget => Variables.CqTimeoutAction == "TransferToTarget" || Variables.CqTimeoutAction == "TransferToVoicemail";
        public bool ShowNoAgentTarget => Variables.CqNoAgentAction == "TransferToTarget" || Variables.CqNoAgentAction == "TransferToVoicemail";
        
        // Voicemail greeting visibility (only show when TransferToVoicemail is selected AND target is a GUID)
        public bool ShowOverflowVoicemailGreeting => Variables.CqOverflowAction == "TransferToVoicemail" && 
            !string.IsNullOrWhiteSpace(Variables.CqOverflowActionTarget) && 
            System.Guid.TryParse(Variables.CqOverflowActionTarget, out _);
        public bool ShowTimeoutVoicemailGreeting => Variables.CqTimeoutAction == "TransferToVoicemail" && 
            !string.IsNullOrWhiteSpace(Variables.CqTimeoutActionTarget) && 
            System.Guid.TryParse(Variables.CqTimeoutActionTarget, out _);
        public bool ShowNoAgentVoicemailGreeting => Variables.CqNoAgentAction == "TransferToVoicemail" && 
            !string.IsNullOrWhiteSpace(Variables.CqNoAgentActionTarget) && 
            System.Guid.TryParse(Variables.CqNoAgentActionTarget, out _);
        
        // Voicemail greeting type visibility
        public bool ShowOverflowVoicemailAudioFile => Variables.CqOverflowVoicemailGreetingType == "AudioFile";
        public bool ShowOverflowVoicemailTextToSpeech => Variables.CqOverflowVoicemailGreetingType == "TextToSpeech";
        public bool ShowTimeoutVoicemailAudioFile => Variables.CqTimeoutVoicemailGreetingType == "AudioFile";
        public bool ShowTimeoutVoicemailTextToSpeech => Variables.CqTimeoutVoicemailGreetingType == "TextToSpeech";
        public bool ShowNoAgentVoicemailAudioFile => Variables.CqNoAgentVoicemailGreetingType == "AudioFile";
        public bool ShowNoAgentVoicemailTextToSpeech => Variables.CqNoAgentVoicemailGreetingType == "TextToSpeech";

        // AA Conditional visibility properties
        public bool ShowAaDefaultGreetingAudioFile => Variables.AaDefaultGreetingType == "AudioFile";
        public bool ShowAaDefaultGreetingTextToSpeech => Variables.AaDefaultGreetingType == "TextToSpeech";
        public bool ShowAaDefaultTarget => Variables.AaDefaultAction == "TransferToTarget" || Variables.AaDefaultAction == "TransferToVoicemail";

        public bool ShowAaAfterHoursGreetingAudioFile => Variables.AaAfterHoursGreetingType == "AudioFile";
        public bool ShowAaAfterHoursGreetingTextToSpeech => Variables.AaAfterHoursGreetingType == "TextToSpeech";
        public bool ShowAaAfterHoursTarget => Variables.AaAfterHoursAction == "TransferToTarget" || Variables.AaAfterHoursAction == "TransferToVoicemail";


        private void UpdateCallQueueVisibility()
        {
            OnPropertyChanged(nameof(ShowGreetingAudioFile));
            OnPropertyChanged(nameof(ShowGreetingTextToSpeech));
            OnPropertyChanged(nameof(ShowMusicOnHoldAudioFile));
            OnPropertyChanged(nameof(ShowOverflowTarget));
            OnPropertyChanged(nameof(ShowTimeoutTarget));
            OnPropertyChanged(nameof(ShowNoAgentTarget));
            OnPropertyChanged(nameof(ShowOverflowVoicemailGreeting));
            OnPropertyChanged(nameof(ShowTimeoutVoicemailGreeting));
            OnPropertyChanged(nameof(ShowNoAgentVoicemailGreeting));
            OnPropertyChanged(nameof(ShowOverflowVoicemailAudioFile));
            OnPropertyChanged(nameof(ShowOverflowVoicemailTextToSpeech));
            OnPropertyChanged(nameof(ShowTimeoutVoicemailAudioFile));
            OnPropertyChanged(nameof(ShowTimeoutVoicemailTextToSpeech));
            OnPropertyChanged(nameof(ShowNoAgentVoicemailAudioFile));
            OnPropertyChanged(nameof(ShowNoAgentVoicemailTextToSpeech));
        }

        private void UpdateAutoAttendantVisibility()
        {
            OnPropertyChanged(nameof(ShowAaDefaultGreetingAudioFile));
            OnPropertyChanged(nameof(ShowAaDefaultGreetingTextToSpeech));
            OnPropertyChanged(nameof(ShowAaDefaultTarget));
            OnPropertyChanged(nameof(ShowAaAfterHoursGreetingAudioFile));
            OnPropertyChanged(nameof(ShowAaAfterHoursGreetingTextToSpeech));
            OnPropertyChanged(nameof(ShowAaAfterHoursTarget));
        }



        // Call Queue Configuration Dialog
        [ObservableProperty]
        private bool _showCallQueueConfigurationDialog = false;

        [RelayCommand]
        private void OpenCallQueueConfigurationDialog()
        {
            ShowCallQueueConfigurationDialog = true;
        }

        [RelayCommand]
        private void CancelCallQueueConfiguration()
        {
            ShowCallQueueConfigurationDialog = false;
        }

        [RelayCommand]
        private void SaveCallQueueConfiguration()
        {
            LogLocalized(UiTextKey.VariablesCallQueueConfigurationSavedLog, "Call queue configuration saved", LogLevel.Info);
            ShowCallQueueConfigurationDialog = false;
        }

        [RelayCommand]
        private async Task SelectGreetingAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.CqGreetingAudioFileId = audioFileId,
                "Greeting");
        }

        [RelayCommand]
        private async Task SelectMusicOnHoldAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.CqMusicOnHoldAudioFileId = audioFileId,
                "Music on Hold");
        }

        [RelayCommand]
        private async Task SelectOverflowVoicemailAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.CqOverflowActionAudioFileId = audioFileId,
                "Overflow Voicemail Greeting");
        }

        [RelayCommand]
        private async Task SelectTimeoutVoicemailAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.CqTimeoutActionAudioFileId = audioFileId,
                "Timeout Voicemail Greeting");
        }

        [RelayCommand]
        private async Task SelectNoAgentVoicemailAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.CqNoAgentActionAudioFileId = audioFileId,
                "No Agent Voicemail Greeting");
        }

        // Auto Attendant Configuration Dialog
        [ObservableProperty]
        private bool _showAutoAttendantConfigurationDialog = false;

        [RelayCommand]
        private void OpenAutoAttendantConfigurationDialog()
        {
            ShowAutoAttendantConfigurationDialog = true;
        }

        [RelayCommand]
        private void CancelAutoAttendantConfiguration()
        {
            ShowAutoAttendantConfigurationDialog = false;
        }

        [RelayCommand]
        private void SaveAutoAttendantConfiguration()
        {
            LogLocalized(UiTextKey.VariablesAutoAttendantConfigurationSavedLog, "Auto attendant configuration saved", LogLevel.Info);
            ShowAutoAttendantConfigurationDialog = false;
        }

        [RelayCommand]
        private async Task SelectAaDefaultGreetingAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.AaDefaultGreetingAudioFileId = audioFileId,
                "AA Default Greeting");
        }

        [RelayCommand]
        private async Task SelectAaAfterHoursGreetingAudioFile()
        {
            await SelectAndImportAudioFile(
                audioFileId => Variables.AaAfterHoursGreetingAudioFileId = audioFileId,
                "AA After Hours Greeting");
        }


        private async Task SelectAndImportAudioFile(Action<string> setAudioFileId, string context)
        {
            try
            {
                var contextLabel = GetAudioContextText(context);
                var window = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                if (window?.MainWindow != null)
                {
                    var storageProvider = window.MainWindow.StorageProvider;
                    
                    var file = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = GetText(
                            UiTextKey.VariablesAudioFilePickerTitle,
                            "Select audio file for {context}",
                            new Dictionary<string, object?> { ["context"] = contextLabel }),
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType(GetText(UiTextKey.VariablesWavFiles, "WAV files")) { Patterns = new[] { "*.wav" } },
                            new Avalonia.Platform.Storage.FilePickerFileType(GetText(UiTextKey.VariablesAllFiles, "All files")) { Patterns = new[] { "*" } }
                        }
                    });

                    if (file != null && file.Count > 0)
                    {
                        var filePath = file[0].Path.LocalPath;
                        var fileInfo = new FileInfo(filePath);
                        
                        // Validate file size (max 5MB)
                        if (fileInfo.Length > 5 * 1024 * 1024)
                        {
                            await _errorHandlingService.HandleGenericError(
                                GetText(UiTextKey.VariablesAudioFileTooLargeMessage, "Audio file must be 5MB or smaller."),
                                GetText(UiTextKey.VariablesAudioFileTooLargeTitle, "File size error"));
                            return;
                        }

                        // Validate file extension
                        if (!filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                        {
                            await _errorHandlingService.HandleGenericError(
                                GetText(UiTextKey.VariablesAudioFileTypeInvalidMessage, "Only WAV files are supported."),
                                GetText(UiTextKey.VariablesAudioFileTypeInvalidTitle, "File type error"));
                            return;
                        }

                        LogLocalized(
                            UiTextKey.VariablesImportAudioFileLog,
                            "Importing audio file for {context}: {path}",
                            LogLevel.Info,
                            new Dictionary<string, object?>
                            {
                                ["context"] = contextLabel,
                                ["path"] = filePath
                            });
                        
                        // Import the audio file via PowerShell
                        var command = _powerShellCommandService.GetImportAudioFileCommand(filePath);
                        var result = await ExecutePowerShellCommandAsync(command, "ImportAudioFile");
                        
                        if (result.HasSuccessMarker)
                        {
                            // Parse the audio file ID from the result
                            var audioFileId = ParseAudioFileIdFromResult(result.Value ?? string.Empty);
                            if (!string.IsNullOrEmpty(audioFileId))
                            {
                                var fileName = fileInfo.Name;
                                setAudioFileId(audioFileId);
                                LogLocalized(
                                    UiTextKey.VariablesAudioFileImportedLog,
                                    "Audio file imported successfully. ID: {id}, File: {file}",
                                    LogLevel.Info,
                                    new Dictionary<string, object?>
                                    {
                                        ["id"] = audioFileId,
                                        ["file"] = fileName
                                    });
                                await _errorHandlingService.ShowSuccess(
                                    GetText(
                                        UiTextKey.VariablesAudioFileImportedMessage,
                                        "Audio file imported successfully.\nFile: {file}\nID: {id}",
                                        new Dictionary<string, object?>
                                        {
                                            ["file"] = fileName,
                                            ["id"] = audioFileId
                                        }),
                                    GetText(UiTextKey.VariablesAudioFileImportedTitle, "Import successful"));
                            }
                            else
                            {
                                await _errorHandlingService.HandleGenericError(
                                    GetText(UiTextKey.VariablesAudioFileIdParseFailedMessage, "Failed to parse the audio file ID from the result."),
                                    GetText(UiTextKey.VariablesAudioFileIdParseFailedTitle, "Import error"));
                            }
                        }
                        else
                        {
                            await _errorHandlingService.HandleGenericError(
                                GetText(
                                    UiTextKey.VariablesAudioFileImportFailedMessage,
                                    "Failed to import the audio file:\n{details}",
                                    new Dictionary<string, object?> { ["details"] = result.Value }),
                                GetText(UiTextKey.VariablesAudioFileImportFailedTitle, "Import error"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.VariablesAudioFileImportFailedLog,
                    "Error importing audio file: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                await _errorHandlingService.HandleGenericError(
                    GetText(
                        UiTextKey.VariablesAudioFileImportFailedMessage,
                        "Error importing the audio file:\n{details}",
                        new Dictionary<string, object?> { ["details"] = ex.Message }),
                    GetText(UiTextKey.VariablesAudioFileImportFailedTitle, "Import error"));
            }
        }

        private string? ParseAudioFileIdFromResult(string result)
        {
            // Look for pattern like "AUDIOFILEID: {guid}" in the result
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("AUDIOFILEID:") && line.Length > 13)
                {
                    var id = line.Substring(13).Trim();
                    if (Guid.TryParse(id, out _))
                    {
                        return id;
                    }
                }
            }
            return null;
        }
    }
} 
