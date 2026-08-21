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
            _loggingService.Log("Holidays page loaded", LogLevel.Info);
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
                
                _loggingService.Log($"Creating holiday series: {holidayName} with {holidayEntries.Count} dates", LogLevel.Info);

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
                    _loggingService.Log($"Holiday series {holidayName} created successfully", LogLevel.Info);
                    IsHolidayCreated = true;
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysCreateHolidaySeriesError,
                        "Error creating holiday series: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    _loggingService.Log($"Error creating holiday series {holidayName}: {result.Value}", LogLevel.Error);
                    IsHolidayCreated = false;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                _loggingService.Log($"Exception in CreateHolidayAsync: {ex}", LogLevel.Error);
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

                _loggingService.Log($"Verifying auto attendant: {AutoAttendantName}", LogLevel.Info);

                var command = _powerShellCommandService.GetVerifyAutoAttendantCommand(AutoAttendantName);
                var result = await ExecutePowerShellCommandAsync(command, null, "VerifyAutoAttendant", allowThrottleRetry: true);
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysVerifyAutoAttendantSuccess,
                        "Auto attendant '{name}' verified successfully and is ready for holiday configuration",
                        new Dictionary<string, object?> { ["name"] = AutoAttendantName });
                    _loggingService.Log($"Auto attendant {AutoAttendantName} verified successfully", LogLevel.Info);
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
                    _loggingService.Log($"Error verifying auto attendant {AutoAttendantName}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                _loggingService.Log($"Exception in VerifyAutoAttendantAsync: {ex}", LogLevel.Error);
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
                _loggingService.Log($"Attaching holiday {holidayName} to auto attendant {AutoAttendantName}", LogLevel.Info);

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
                    _loggingService.Log($"Successfully attached holiday {holidayName} to auto attendant {AutoAttendantName}", LogLevel.Info);
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidaysAttachHolidayError,
                        "Error attaching holiday to auto attendant: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    _loggingService.Log($"Error attaching holiday {holidayName} to auto attendant {AutoAttendantName}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                _loggingService.Log($"Exception in AttachHolidayToAutoAttendantAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
} 
