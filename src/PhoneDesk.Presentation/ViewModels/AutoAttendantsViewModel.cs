using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Models;
using System;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

namespace PhoneDesk.ViewModels
{
    public partial class AutoAttendantsViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<ResourceAccount> _resourceAccounts = new();

        [ObservableProperty]
        private ObservableCollection<AutoAttendant> _autoAttendants = new();

        [ObservableProperty]
        private string _searchResourceAccountsText = string.Empty;

        [ObservableProperty]
        private string _searchAutoAttendantsText = string.Empty;

        [ObservableProperty]
        private bool _showCreateResourceAccountDialog;

        [ObservableProperty]
        private bool _showUpdateUsageLocationDialog;

        [ObservableProperty]
        private bool _showCreateAutoAttendantDialog;

        [ObservableProperty]
        private bool _showAssociateDialog;

        [ObservableProperty]
        private bool _showValidateCallQueueDialog;

        [ObservableProperty]
        private bool _showCreateCallTargetDialog;

        [ObservableProperty]
        private bool _showCreateDefaultCallFlowDialog;

        [ObservableProperty]
        private bool _showCreateAfterHoursCallFlowDialog;

        [ObservableProperty]
        private bool _showCreateAfterHoursScheduleDialog;

        [ObservableProperty]
        private bool _showCreateCallHandlingAssociationDialog;

        [ObservableProperty]
        private string _resourceAccountUpn = string.Empty;

        [ObservableProperty]
        private string _resourceAccountDisplayName = string.Empty;

        [ObservableProperty]
        private string _autoAttendantName = string.Empty;

        [ObservableProperty]
        private string _callQueueUpn = string.Empty;

        [ObservableProperty]
        private string _m365GroupId = string.Empty;

        public ObservableCollection<ResourceAccount> ResourceAccountsView => new ObservableCollection<ResourceAccount>(
            ResourceAccounts.Where(FilterResourceAccount)
        );
        public ObservableCollection<AutoAttendant> AutoAttendantsView => new ObservableCollection<AutoAttendant>(
            AutoAttendants.Where(FilterAutoAttendant)
        );

        public AutoAttendantsViewModel(
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
            LogLocalized(
                UiTextKey.AutoAttendantsPageLoadedLog,
                "Auto attendants page loaded",
                LogLevel.Info);

            ResourceAccounts.CollectionChanged += (s, e) => OnPropertyChanged(nameof(ResourceAccountsView));
            AutoAttendants.CollectionChanged += (s, e) => OnPropertyChanged(nameof(AutoAttendantsView));
        }

        [RelayCommand]
        private async Task RetrieveResourceAccountsAsync()
        {
            try
            {
                IsBusy = true;
                ResourceAccounts.Clear();
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsLoadResourceAccountsStatus,
                    "Loading resource accounts...");

                LogLocalized(
                    UiTextKey.AutoAttendantsLoadResourceAccountsLog,
                    "Loading resource accounts starting with '{prefix}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["prefix"] = "raaa-" });

                var command = _powerShellCommandService.GetRetrieveAutoAttendantResourceAccountsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveAutoAttendantResourceAccounts", allowThrottleRetry: true);

                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseResourceAccountsFromResult(result.Value);
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsLoadResourceAccountsSuccess,
                        "Loaded {count} resource accounts starting with '{prefix}'.",
                        new Dictionary<string, object?> { ["count"] = ResourceAccounts.Count, ["prefix"] = "raaa-" });
                    LogLocalized(
                        UiTextKey.AutoAttendantsLoadResourceAccountsSuccess,
                        "Loaded {count} resource accounts starting with '{prefix}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["count"] = ResourceAccounts.Count, ["prefix"] = "raaa-" });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.",
                        LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(RetrieveResourceAccountsAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RetrieveAutoAttendantsAsync()
        {
            try
            {
                IsBusy = true;
                AutoAttendants.Clear();
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsLoadAutoAttendantsStatus,
                    "Loading auto attendants...");

                LogLocalized(
                    UiTextKey.AutoAttendantsLoadAutoAttendantsLog,
                    "Loading auto attendants containing '{pattern}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["pattern"] = "aa-" });

                var command = _powerShellCommandService.GetRetrieveAutoAttendantsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveAutoAttendants", allowThrottleRetry: true);

                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseAutoAttendantsFromResult(result.Value);
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsLoadAutoAttendantsSuccess,
                        "Loaded {count} auto attendants containing '{pattern}'.",
                        new Dictionary<string, object?> { ["count"] = AutoAttendants.Count, ["pattern"] = "aa-" });
                    LogLocalized(
                        UiTextKey.AutoAttendantsLoadAutoAttendantsSuccess,
                        "Loaded {count} auto attendants containing '{pattern}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["count"] = AutoAttendants.Count, ["pattern"] = "aa-" });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.",
                        LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(RetrieveAutoAttendantsAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenCreateResourceAccountDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                ResourceAccountUpn = variables.RaaaUPN;
                ResourceAccountDisplayName = variables.RaaaDisplayName;
            }
            else
            {
                ResourceAccountUpn = "raaa-";
                ResourceAccountDisplayName = "raaa-";
            }
            ShowCreateResourceAccountDialog = true;
        }

        [RelayCommand]
        private void CloseCreateResourceAccountDialog()
        {
            ShowCreateResourceAccountDialog = false;
        }

        [RelayCommand]
        private void CloseUpdateUsageLocationDialog()
        {
            ShowUpdateUsageLocationDialog = false;
        }


        [RelayCommand]
        private void CloseCreateAutoAttendantDialog()
        {
            ShowCreateAutoAttendantDialog = false;
        }

        [RelayCommand]
        private void CloseAssociateDialog()
        {
            ShowAssociateDialog = false;
        }

        [RelayCommand]
        private void OpenValidateCallQueueDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                CallQueueUpn = variables.RacqUPN;
            }
            else
            {
                CallQueueUpn = "racq-";
            }
            ShowValidateCallQueueDialog = true;
        }

        [RelayCommand]
        private void CloseValidateCallQueueDialog()
        {
            ShowValidateCallQueueDialog = false;
        }

        [RelayCommand]
        private void OpenCreateCallTargetDialog()
        {
            ShowCreateCallTargetDialog = true;
        }

        [RelayCommand]
        private void CloseCreateCallTargetDialog()
        {
            ShowCreateCallTargetDialog = false;
        }

        [RelayCommand]
        private void OpenCreateDefaultCallFlowDialog()
        {
            ShowCreateDefaultCallFlowDialog = true;
        }

        [RelayCommand]
        private void CloseCreateDefaultCallFlowDialog()
        {
            ShowCreateDefaultCallFlowDialog = false;
        }

        [RelayCommand]
        private void OpenCreateAfterHoursCallFlowDialog()
        {
            ShowCreateAfterHoursCallFlowDialog = true;
        }

        [RelayCommand]
        private void CloseCreateAfterHoursCallFlowDialog()
        {
            ShowCreateAfterHoursCallFlowDialog = false;
        }

        [RelayCommand]
        private void OpenCreateAfterHoursScheduleDialog()
        {
            ShowCreateAfterHoursScheduleDialog = true;
        }

        [RelayCommand]
        private void CloseCreateAfterHoursScheduleDialog()
        {
            ShowCreateAfterHoursScheduleDialog = false;
        }

        [RelayCommand]
        private void OpenCreateCallHandlingAssociationDialog()
        {
            ShowCreateCallHandlingAssociationDialog = true;
        }

        [RelayCommand]
        private void CloseCreateCallHandlingAssociationDialog()
        {
            ShowCreateCallHandlingAssociationDialog = false;
        }

        [RelayCommand]
        private async Task CreateResourceAccountAsync()
        {
            if (string.IsNullOrWhiteSpace(ResourceAccountUpn) || string.IsNullOrWhiteSpace(ResourceAccountDisplayName))
            {
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsResourceAccountDetailsRequiredError,
                    "Error: Resource account UPN and display name cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;
                ShowCreateResourceAccountDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeVariablesNotFoundError,
                        "Error: Configuration not found.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(variables.CsAppAaId))
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsApplicationIdMissingError,
                        "Error: Auto attendant application ID was not found in Configuration.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(variables.MsFallbackDomain) || !variables.MsFallbackDomain.StartsWith("@"))
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeFallbackDomainInvalidError,
                        "Error: Microsoft fallback domain is not set or is invalid. Set a valid domain such as @yourdomain.com in Configuration.");
                    return;
                }

                // Build UPN: if user typed a value without @, append the domain
                var upn = ResourceAccountUpn;
                if (!upn.Contains("@"))
                {
                    upn = upn + variables.MsFallbackDomain;
                }

                var command = _powerShellCommandService.GetCreateAutoAttendantResourceAccountCommand(upn, ResourceAccountDisplayName, variables.CsAppAaId);
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.AutoAttendantsCreateResourceAccountContext,
                        "Create auto attendant resource account"));

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
                        UiTextKey.AutoAttendantsCreateResourceAccountSuccess,
                        "Resource account '{upn}' created successfully.",
                        new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });
                    LogLocalized(
                        UiTextKey.AutoAttendantsCreateResourceAccountLog,
                        "Resource account '{upn}' created successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveResourceAccountsAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsCreateResourceAccountError,
                        "Error creating the resource account: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.AutoAttendantsCreateResourceAccountError,
                        "Error creating the resource account: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(CreateResourceAccountAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenUpdateUsageLocationDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                ResourceAccountUpn = variables.RaaaUPN;
            }
            else
            {
                ResourceAccountUpn = "raaa-";
            }
            ShowUpdateUsageLocationDialog = true;
        }

        [RelayCommand]
        private void OpenCreateAutoAttendantDialog()
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
            ShowCreateAutoAttendantDialog = true;
        }

        [RelayCommand]
        private async Task CreateAutoAttendantAsync()
        {
            if (string.IsNullOrWhiteSpace(AutoAttendantName))
            {
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsNameRequiredError,
                    "Error: Auto attendant name cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;
                ShowCreateAutoAttendantDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeVariablesNotFoundError,
                        "Error: Configuration not found.");
                    return;
                }

                LogLocalized(
                    UiTextKey.AutoAttendantsCreateAutoAttendantLog,
                    "Creating auto attendant '{name}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["name"] = AutoAttendantName });

                var command = _powerShellCommandService.GetCreateSimpleAutoAttendantCommand(AutoAttendantName, variables.LanguageId, variables.TimeZoneId);
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.AutoAttendantsCreateAutoAttendantContext,
                        "Create auto attendant"));

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
                        UiTextKey.AutoAttendantsCreateAutoAttendantSuccess,
                        "Auto attendant '{name}' created successfully.",
                        new Dictionary<string, object?> { ["name"] = AutoAttendantName });
                    LogLocalized(
                        UiTextKey.AutoAttendantsCreateAutoAttendantSuccessLog,
                        "Auto attendant '{name}' created successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = AutoAttendantName });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveAutoAttendantsAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsCreateAutoAttendantError,
                        "Error creating the auto attendant: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.AutoAttendantsCreateAutoAttendantError,
                        "Error creating the auto attendant: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(CreateAutoAttendantAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenAssociateDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null)
            {
                ResourceAccountUpn = variables.RaaaUPN;
                AutoAttendantName = variables.AaDisplayName;
            }
            else
            {
                ResourceAccountUpn = "raaa-";
                AutoAttendantName = "aa-";
            }
            ShowAssociateDialog = true;
        }

        [RelayCommand]
        private async Task RemoveAutoAttendantAsync(string? aaName = null)
        {
            var name = aaName ?? AutoAttendantName;
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsNameRequiredError,
                    "Error: Auto attendant name cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveAutoAttendantCommand(name);
                var result = await ConfirmAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.AutoAttendantsDeleteAutoAttendantConfirm,
                        "This permanently deletes the auto attendant '{name}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["name"] = name }),
                    GetText(
                        UiTextKey.AutoAttendantsDeleteAutoAttendantContext,
                        "Delete auto attendant"));

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
                        UiTextKey.AutoAttendantsDeleteAutoAttendantSuccess,
                        "Auto attendant '{name}' deleted successfully.",
                        new Dictionary<string, object?> { ["name"] = name });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteAutoAttendantLog,
                        "Auto attendant '{name}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = name });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveAutoAttendantsAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsDeleteAutoAttendantError,
                        "Error deleting the auto attendant: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteAutoAttendantError,
                        "Error deleting the auto attendant: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(RemoveAutoAttendantAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RemoveScheduleAsync(string scheduleName)
        {
            if (string.IsNullOrWhiteSpace(scheduleName))
            {
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsScheduleNameRequiredError,
                    "Error: Schedule name cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveScheduleCommand(scheduleName);
                var result = await ConfirmAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.AutoAttendantsDeleteScheduleConfirm,
                        "This permanently deletes the schedule '{name}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["name"] = scheduleName }),
                    GetText(
                        UiTextKey.AutoAttendantsDeleteScheduleContext,
                        "Delete schedule"));

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
                        UiTextKey.AutoAttendantsDeleteScheduleSuccess,
                        "Schedule '{name}' deleted successfully.",
                        new Dictionary<string, object?> { ["name"] = scheduleName });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteScheduleLog,
                        "Schedule '{name}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = scheduleName });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsDeleteScheduleError,
                        "Error deleting the schedule: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteScheduleError,
                        "Error deleting the schedule: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(RemoveScheduleAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RemoveResourceAccountAsync(string? upn = null)
        {
            var accountUpn = upn ?? ResourceAccountUpn;
            if (string.IsNullOrWhiteSpace(accountUpn))
            {
                StatusMessage = GetText(
                    UiTextKey.AutoAttendantsResourceAccountUpnRequiredError,
                    "Error: Resource account UPN cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveResourceAccountCommand(accountUpn);
                var result = await ConfirmAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.AutoAttendantsDeleteResourceAccountConfirm,
                        "This permanently deletes the resource account '{upn}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["upn"] = accountUpn }),
                    GetText(
                        UiTextKey.AutoAttendantsDeleteResourceAccountContext,
                        "Delete resource account"));

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
                        UiTextKey.AutoAttendantsDeleteResourceAccountSuccess,
                        "Resource account '{upn}' deleted successfully.",
                        new Dictionary<string, object?> { ["upn"] = accountUpn });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteResourceAccountLog,
                        "Resource account '{upn}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = accountUpn });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveResourceAccountsAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.AutoAttendantsDeleteResourceAccountError,
                        "Error deleting the resource account: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.AutoAttendantsDeleteResourceAccountError,
                        "Error deleting the resource account: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(RemoveResourceAccountAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ParseResourceAccountsFromResult(string result)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.StartsWith("RESOURCEACCOUNT:") && line.Length > 16)
                {
                    var accountData = line.Substring(16).Split('|');
                    if (accountData.Length >= 4)
                    {
                        var account = new ResourceAccount(
                            accountData[0].Trim(),
                            accountData[1].Trim(),
                            accountData[2].Trim(),
                            accountData[3].Trim()
                        );
                        ResourceAccounts.Add(account);
                    }
                }
            }
            OnPropertyChanged(nameof(ResourceAccountsView));
        }

        private void ParseAutoAttendantsFromResult(string result)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.StartsWith("AUTOATTENDANT:") && line.Length > 14)
                {
                    var aaData = line.Substring(14).Split('|');
                    if (aaData.Length >= 4)
                    {
                        var autoAttendant = new AutoAttendant(
                            aaData[0].Trim(),
                            aaData[1].Trim(),
                            aaData[2].Trim(),
                            aaData[3].Trim()
                        );
                        AutoAttendants.Add(autoAttendant);
                    }
                }
            }
            OnPropertyChanged(nameof(AutoAttendantsView));
        }

        private bool FilterResourceAccount(object obj)
        {
            if (obj is not ResourceAccount account)
                return false;

            if (string.IsNullOrWhiteSpace(SearchResourceAccountsText))
                return true;

            var query = SearchResourceAccountsText.Trim();
            return (account.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (account.UserPrincipalName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (account.Identity?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (account.UsageLocation?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private bool FilterAutoAttendant(object obj)
        {
            if (obj is not AutoAttendant autoAttendant)
                return false;

            if (string.IsNullOrWhiteSpace(SearchAutoAttendantsText))
                return true;

            var query = SearchAutoAttendantsText.Trim();
            return (autoAttendant.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (autoAttendant.Identity?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (autoAttendant.LanguageId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (autoAttendant.TimeZoneId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        partial void OnSearchResourceAccountsTextChanged(string value)
        {
            OnPropertyChanged(nameof(ResourceAccountsView));
        }

        partial void OnSearchAutoAttendantsTextChanged(string value)
        {
            OnPropertyChanged(nameof(AutoAttendantsView));
        }
    }
} 
