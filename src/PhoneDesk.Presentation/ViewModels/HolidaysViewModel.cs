using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using PhoneDesk.Models;
using PhoneDesk.Localization;
using System;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

namespace PhoneDesk.ViewModels
{
    public partial class HolidaysViewModel : ViewModelBase
    {
        [ObservableProperty]
        private bool _showCreateHolidayDialog;

        [ObservableProperty]
        private bool _showCheckAutoAttendantDialog;

        [ObservableProperty]
        private bool _showAttachHolidayDialog;

        [ObservableProperty]
        private string _holidayName = string.Empty;

        [ObservableProperty]
        private DateTime _holidayDate = DateTime.Now;


        [ObservableProperty]
        private string _autoAttendantName = string.Empty;

        [ObservableProperty]
        private bool _isHolidayCreated = false;

        public HolidaysViewModel(
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
            LogLocalized(UiTextKey.HolidaysPageLoadedLog, "Holidays page loaded", LogLevel.Info);
        }

        [RelayCommand]
        private void ResetHolidayState()
        {
            IsHolidayCreated = false;
            HolidayName = string.Empty;
            HolidayDate = DateTime.Now;
            StatusMessage = GetText(
                UiTextKey.HolidaysResetStateStatus,
                "Holiday state reset. You can now create a new holiday.");
        }

        [RelayCommand]
        private void OpenCreateHolidayDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                HolidayName = variables.HolidayName;
                HolidayDate = variables.HolidayDate;
            }
            else
            {
                HolidayName = "hd-";
                HolidayDate = DateTime.Now;
            }
            ShowCreateHolidayDialog = true;
        }

        [RelayCommand]
        private void CloseCreateHolidayDialog()
        {
            ShowCreateHolidayDialog = false;
        }

        [RelayCommand]
        private async Task CreateHolidayAsync()
        {
            try
            {
                IsBusy = true;
                ShowCreateHolidayDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysVariablesNotFoundError,
                        "Error: Variables not found");
                    return;
                }

                if (variables.HolidaySeries.Count == 0)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysNoHolidaysConfiguredError,
                        "Error: No holidays configured. Please add holidays in the Variables page first.");
                    return;
                }

                // Create a single holiday schedule with multiple date ranges (supports end dates)
                var holidayEntries = variables.HolidaySeries.ToList();
                var holidayName = variables.HolidayName;
                
                LogLocalized(
                    UiTextKey.HolidaysCreateHolidaySeriesLog,
                    "Creating holiday series '{name}' with {count} dates",
                    LogLevel.Info,
                    new Dictionary<string, object?>
                    {
                        ["name"] = holidayName,
                        ["count"] = holidayEntries.Count
                    });

                var command = _powerShellCommandService.GetCreateHolidaySeriesFromEntriesCommand(holidayName, holidayEntries);
                var result = await PreviewAndExecuteAsync(command, "Create Holiday Series");
                
                if (result == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeOperationCancelledByUser,
                        "Operation cancelled by user");
                    return;
                }
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysHolidaySeriesCreatedSuccess,
                        "Holiday series '{name}' created successfully with {count} dates!",
                        new Dictionary<string, object?>
                        {
                            ["name"] = holidayName,
                            ["count"] = holidayEntries.Count
                        });
                    LogLocalized(
                        UiTextKey.HolidaysCreateHolidaySeriesSuccessLog,
                        "Holiday series '{name}' created successfully",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = holidayName });
                    IsHolidayCreated = true;
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysCreateHolidaySeriesError,
                        "Error creating holiday series: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.HolidaysCreateHolidaySeriesErrorLog,
                        "Error creating holiday series '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["name"] = holidayName,
                            ["details"] = result.Value
                        });
                    IsHolidayCreated = false;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogException(nameof(CreateHolidayAsync), ex);
                IsHolidayCreated = false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenCheckAutoAttendantDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                AutoAttendantName = variables.AaDisplayName;
            }
            else
            {
                AutoAttendantName = "aa-";
            }
            ShowCheckAutoAttendantDialog = true;
        }

        [RelayCommand]
        private void CloseCheckAutoAttendantDialog()
        {
            ShowCheckAutoAttendantDialog = false;
        }

        [RelayCommand]
        private async Task VerifyAutoAttendantAsync()
        {
            if (string.IsNullOrWhiteSpace(AutoAttendantName))
            {
                StatusMessage = GetText(
                    UiTextKey.HolidaysAutoAttendantNameRequiredError,
                    "Error: Auto attendant name cannot be empty");
                return;
            }

            try
            {
                IsBusy = true;
                ShowCheckAutoAttendantDialog = false;

                LogLocalized(
                    UiTextKey.HolidaysVerifyAutoAttendantLog,
                    "Verifying auto attendant '{name}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["name"] = AutoAttendantName });

                var command = _powerShellCommandService.GetVerifyAutoAttendantCommand(AutoAttendantName);
                var result = await ExecutePowerShellCommandAsync(command, null, "VerifyAutoAttendant", allowThrottleRetry: true);
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysVerifyAutoAttendantSuccess,
                        "Auto attendant '{name}' verified successfully and is ready for holiday configuration",
                        new Dictionary<string, object?> { ["name"] = AutoAttendantName });
                    LogLocalized(
                        UiTextKey.HolidaysVerifyAutoAttendantSuccessLog,
                        "Auto attendant '{name}' verified successfully",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = AutoAttendantName });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysVerifyAutoAttendantMissingError,
                        "Error: Auto attendant '{name}' not found or not accessible: {details}",
                        new Dictionary<string, object?>
                        {
                            ["name"] = AutoAttendantName,
                            ["details"] = result.Value
                        });
                    LogLocalized(
                        UiTextKey.HolidaysVerifyAutoAttendantErrorLog,
                        "Error verifying auto attendant '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["name"] = AutoAttendantName,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogException(nameof(VerifyAutoAttendantAsync), ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenAttachHolidayDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                AutoAttendantName = variables.AaDisplayName;
                HolidayName = variables.HolidayName;
            }
            else
            {
                AutoAttendantName = "aa-";
                HolidayName = "hd-";
            }
            ShowAttachHolidayDialog = true;
        }

        [RelayCommand]
        private void CloseAttachHolidayDialog()
        {
            ShowAttachHolidayDialog = false;
        }

        [RelayCommand]
        private async Task AttachHolidayToAutoAttendantAsync()
        {
            if (string.IsNullOrWhiteSpace(AutoAttendantName))
            {
                StatusMessage = GetText(
                    UiTextKey.HolidaysAutoAttendantNameRequiredError,
                    "Error: Auto attendant name cannot be empty");
                return;
            }

            try
            {
                IsBusy = true;
                ShowAttachHolidayDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysVariablesNotFoundError,
                        "Error: Variables not found");
                    return;
                }

                var holidayName = HolidayName;
                LogLocalized(
                    UiTextKey.HolidaysAttachHolidayLog,
                    "Attaching holiday '{holidayName}' to auto attendant '{name}'",
                    LogLevel.Info,
                    new Dictionary<string, object?>
                    {
                        ["holidayName"] = holidayName,
                        ["name"] = AutoAttendantName
                    });

                var command = _powerShellCommandService.GetAttachHolidayToAutoAttendantCommand(holidayName, AutoAttendantName, variables.HolidayGreetingPromptDE);
                var result = await PreviewAndExecuteAsync(command, "Attach Holiday to Auto Attendant");
                
                if (result == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeOperationCancelledByUser,
                        "Operation cancelled by user");
                    return;
                }
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysAttachHolidaySuccess,
                        "Successfully attached holiday '{holidayName}' to auto attendant '{name}'",
                        new Dictionary<string, object?>
                        {
                            ["holidayName"] = holidayName,
                            ["name"] = AutoAttendantName
                        });
                    LogLocalized(
                        UiTextKey.HolidaysAttachHolidaySuccessLog,
                        "Holiday '{holidayName}' attached to auto attendant '{name}' successfully",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["holidayName"] = holidayName,
                            ["name"] = AutoAttendantName
                        });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysAttachHolidayError,
                        "Error attaching holiday to auto attendant: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.HolidaysAttachHolidayErrorLog,
                        "Error attaching holiday '{holidayName}' to auto attendant '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["holidayName"] = holidayName,
                            ["name"] = AutoAttendantName,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogException(nameof(AttachHolidayToAutoAttendantAsync), ex);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
} 
