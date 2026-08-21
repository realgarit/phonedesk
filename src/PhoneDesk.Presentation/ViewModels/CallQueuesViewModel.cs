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
    public partial class CallQueuesViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<ResourceAccount> _resourceAccounts = new();

        [ObservableProperty]
        private ObservableCollection<CallQueue> _callQueues = new();

        [ObservableProperty]
        private string _searchResourceAccountsText = string.Empty;

        [ObservableProperty]
        private string _searchCallQueuesText = string.Empty;

        [ObservableProperty]
        private bool _showCreateResourceAccountDialog;

        [ObservableProperty]
        private bool _showUpdateUsageLocationDialog;

        [ObservableProperty]
        private bool _showCreateCallQueueDialog;

        [ObservableProperty]
        private bool _showAssociateDialog;

        [ObservableProperty]
        private string _resourceAccountUpn = string.Empty;

        [ObservableProperty]
        private string _resourceAccountDisplayName = string.Empty;

        [ObservableProperty]
        private string _callQueueName = string.Empty;

        [ObservableProperty]
        private string _m365GroupId = string.Empty;

        public ObservableCollection<ResourceAccount> ResourceAccountsView => new ObservableCollection<ResourceAccount>(
            ResourceAccounts.Where(FilterResourceAccount)
        );
        public ObservableCollection<CallQueue> CallQueuesView => new ObservableCollection<CallQueue>(
            CallQueues.Where(FilterCallQueue)
        );

        public CallQueuesViewModel(
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
            _loggingService.Log("Call Queues page loaded", LogLevel.Info);

            ResourceAccounts.CollectionChanged += (s, e) => OnPropertyChanged(nameof(ResourceAccountsView));
            CallQueues.CollectionChanged += (s, e) => OnPropertyChanged(nameof(CallQueuesView));
        }

        [RelayCommand]
        private async Task RetrieveResourceAccountsAsync()
        {
            try
            {
                SetWaiting(
                    UiTextKey.RuntimeWorkingPleaseWait,
                    "Please wait while the previous operation is processed by Microsoft.");
                IsBusy = true;
                ResourceAccounts.Clear();
                StatusMessage = "Retrieving resource accounts...";

                _loggingService.Log("Retrieving resource accounts starting with 'racq-'", LogLevel.Info);

                var command = _powerShellCommandService.GetRetrieveResourceAccountsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveResourceAccounts", allowThrottleRetry: true);
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseResourceAccountsFromResult(result.Value);
                    StatusMessage = $"Found {ResourceAccounts.Count} resource accounts starting with 'racq-'";
                    _loggingService.Log($"Retrieved {ResourceAccounts.Count} resource accounts", LogLevel.Info);
                }
                else
                {
                    StatusMessage = "Error: No output from PowerShell command";
                    _loggingService.Log("Error retrieving resource accounts: No output from PowerShell command", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in RetrieveResourceAccountsAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RetrieveCallQueuesAsync()
        {
            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                CallQueues.Clear();
                StatusMessage = "Retrieving call queues...";

                _loggingService.Log("Retrieving call queues containing 'cq-'", LogLevel.Info);

                var command = _powerShellCommandService.GetRetrieveCallQueuesCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveCallQueues", allowThrottleRetry: true);
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseCallQueuesFromResult(result.Value);
                    StatusMessage = $"Found {CallQueues.Count} call queues containing 'cq-'";
                    _loggingService.Log($"Retrieved {CallQueues.Count} call queues", LogLevel.Info);
                }
                else
                {
                    StatusMessage = "Error: No output from PowerShell command";
                    _loggingService.Log("Error retrieving call queues: No output from PowerShell command", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in RetrieveCallQueuesAsync: {ex}", LogLevel.Error);
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
                ResourceAccountUpn = variables.RacqUPN;
                ResourceAccountDisplayName = variables.RacqDisplayName;
            }
            else
            {
                ResourceAccountUpn = "racq-";
                ResourceAccountDisplayName = "racq-";
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
        private void CloseCreateCallQueueDialog()
        {
            ShowCreateCallQueueDialog = false;
        }

        [RelayCommand]
        private void CloseAssociateDialog()
        {
            ShowAssociateDialog = false;
        }

        [RelayCommand]
        private async Task AssignLicenseAsync()
        {
            var variables = _sharedStateService?.Variables;
            if (variables == null)
            {
                StatusMessage = GetText(
                    UiTextKey.RuntimeVariablesNotFoundError,
                    "Error: Configuration not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(variables.RacqUPN))
            {
                StatusMessage = GetText(
                    UiTextKey.CallQueuesResourceAccountUpnMissingError,
                    "Error: Resource account UPN is not set. Set the configuration first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(variables.SkuId))
            {
                StatusMessage = GetText(
                    UiTextKey.CallQueuesSkuIdMissingError,
                    "Error: SKU ID is not set. Set the SKU ID in Configuration first.");
                return;
            }

            try
            {
                SetWaiting(
                    UiTextKey.RuntimeApplyingLicensePleaseWait,
                    "Please wait while the Teams Phone Resource License is being applied.");
                IsBusy = true;
                StatusMessage = GetText(
                    UiTextKey.CallQueuesAssignLicenseStatus,
                    "Assigning the license to the resource account...");

                var command = _powerShellCommandService.GetAssignLicenseCommand(variables.RacqUPN, variables.SkuId);
                var result = await ExecutePowerShellCommandAsync(command, "AssignLicense");
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesAssignLicenseSuccess,
                        "License assigned to resource account '{upn}' successfully.",
                        new Dictionary<string, object?> { ["upn"] = variables.RacqUPN });
                    LogLocalized(
                        UiTextKey.CallQueuesAssignLicenseLog,
                        "License assigned to resource account '{upn}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = variables.RacqUPN });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesAssignLicenseError,
                        "Error assigning the license: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    _loggingService.Log($"Error assigning license to {variables.RacqUPN}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                _loggingService.Log($"Exception in AssignLicenseAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task GetM365GroupIdAsync()
        {
            var variables = _sharedStateService?.Variables;
            if (variables == null)
            {
                StatusMessage = "Error: Variables not found";
                return;
            }

            if (string.IsNullOrWhiteSpace(variables.M365Group))
            {
                StatusMessage = "Error: M365 Group name is not set. Please set Customer and Customer Group Name variables first.";
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                StatusMessage = "Retrieving M365 Group ID...";

                var command = _powerShellCommandService.GetM365GroupIdCommand(variables.M365Group);
                var result = await ExecutePowerShellCommandAsync(command, null, "GetM365GroupId", allowThrottleRetry: true);
                
                if (result.HasSuccessMarker)
                {
                    // Parse the group ID from the result
                    var lines = (result.Value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("M365GROUPID:") && line.Length > 12)
                        {
                            var groupId = line.Substring(12).Trim();
                            variables.M365GroupId = groupId;
                            StatusMessage = $"M365 Group ID retrieved and saved: {groupId}";
                            _loggingService.Log($"M365 Group ID saved: {groupId}", LogLevel.Info);
                            return;
                        }
                    }
                    StatusMessage = "Error: Could not parse M365 Group ID from result";
                    _loggingService.Log("Error: Could not parse M365 Group ID from result - no M365GROUPID line found", LogLevel.Error);
                }
                else
                {
                    StatusMessage = $"Error retrieving M365 Group ID: {result.Value}";
                    _loggingService.Log($"Error retrieving M365 Group ID: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in GetM365GroupIdAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task CreateResourceAccountAsync()
        {
            if (string.IsNullOrWhiteSpace(ResourceAccountUpn) || string.IsNullOrWhiteSpace(ResourceAccountDisplayName))
            {
                StatusMessage = "Error: Resource account UPN and display name cannot be empty";
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                ShowCreateResourceAccountDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = "Error: Variables not found";
                    return;
                }

                if (string.IsNullOrWhiteSpace(variables.CsAppCqId))
                {
                    StatusMessage = "Error: Call Queue Application ID not found in variables";
                    return;
                }

                // Validate that the UPN includes a domain
                if (string.IsNullOrWhiteSpace(variables.MsFallbackDomain) || !variables.MsFallbackDomain.StartsWith("@"))
                {
                    StatusMessage = "Error: MS Fallback Domain is not set or invalid. Please set a valid domain (e.g., @yourdomain.com) in Variables.";
                    return;
                }

                // Build UPN: if user typed a value without @, append the domain
                var upn = ResourceAccountUpn;
                if (!upn.Contains("@"))
                {
                    upn = upn + variables.MsFallbackDomain;
                }

                var command = _powerShellCommandService.GetCreateResourceAccountCommand(upn, ResourceAccountDisplayName, variables.CsAppCqId);
                var result = await PreviewAndExecuteAsync(command, "Create Resource Account");
                
                if (result == null)
                {
                    StatusMessage = "Operation cancelled by user";
                    return;
                }
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = $"Resource account '{ResourceAccountUpn}' created successfully";
                    _loggingService.Log($"Resource account {ResourceAccountUpn} created successfully", LogLevel.Info);
                    // Don't auto-refresh to avoid showing "Found ... resource accounts" message
                }
                else
                {
                    StatusMessage = $"Error creating resource account: {result.Value}";
                    _loggingService.Log($"Error creating resource account {ResourceAccountUpn}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in CreateResourceAccountAsync: {ex}", LogLevel.Error);
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
                ResourceAccountUpn = variables.RacqUPN;
            }
            else
            {
                ResourceAccountUpn = "racq-";
            }
            ShowUpdateUsageLocationDialog = true;
        }

        [RelayCommand]
        private async Task UpdateUsageLocationAsync()
        {
            if (string.IsNullOrWhiteSpace(ResourceAccountUpn))
            {
                StatusMessage = "Error: Resource account UPN cannot be empty";
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                ShowUpdateUsageLocationDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = "Error: Variables not found";
                    return;
                }

                _loggingService.Log($"Updating usage location for: {ResourceAccountUpn}", LogLevel.Info);

                var command = _powerShellCommandService.GetUpdateResourceAccountUsageLocationCommand(ResourceAccountUpn, variables.UsageLocation);
                var result = await ExecutePowerShellCommandAsync(command, "UpdateUsageLocation");
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = $"Usage location updated for '{ResourceAccountUpn}' to '{variables.UsageLocation}'";
                    _loggingService.Log($"Usage location updated for {ResourceAccountUpn}", LogLevel.Info);
                    // Don't auto-refresh to avoid showing "Found ... resource accounts" message
                }
                else
                {
                    StatusMessage = $"Error updating usage location: {result.Value}";
                    _loggingService.Log($"Error updating usage location for {ResourceAccountUpn}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in UpdateUsageLocationAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenCreateCallQueueDialog()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                CallQueueName = variables.CqDisplayName;
                M365GroupId = variables.M365GroupId; // Use the actual group ID instead of name
            }
            else
            {
                CallQueueName = "cq-";
                M365GroupId = "";
            }
            ShowCreateCallQueueDialog = true;
        }

        [RelayCommand]
        private async Task CreateCallQueueAsync()
        {
            if (string.IsNullOrWhiteSpace(CallQueueName))
            {
                StatusMessage = "Error: Call queue name cannot be empty";
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                ShowCreateCallQueueDialog = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    StatusMessage = "Error: Variables not found";
                    return;
                }

                _loggingService.Log($"Creating call queue: {CallQueueName}", LogLevel.Info);

                var command = _powerShellCommandService.GetCreateCallQueueCommand(CallQueueName, variables.LanguageId, variables.M365GroupId, variables);
                var result = await PreviewAndExecuteAsync(command, "Create Call Queue");
                
                if (result == null)
                {
                    StatusMessage = "Operation cancelled by user";
                    return;
                }
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = $"Call queue '{CallQueueName}' created successfully";
                    _loggingService.Log($"Call queue {CallQueueName} created successfully", LogLevel.Info);
                    // Don't auto-refresh to avoid showing "Found ... call queues" message
                }
                else
                {
                    StatusMessage = $"Error creating call queue: {result.Value}";
                    _loggingService.Log($"Error creating call queue {CallQueueName}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in CreateCallQueueAsync: {ex}", LogLevel.Error);
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
                ResourceAccountUpn = variables.RacqUPN;
                CallQueueName = variables.CqDisplayName;
            }
            else
            {
                ResourceAccountUpn = "racq-";
                CallQueueName = "cq-";
            }
            ShowAssociateDialog = true;
        }

        [RelayCommand]
        private async Task AssociateResourceAccountWithCallQueueAsync()
        {
            if (string.IsNullOrWhiteSpace(ResourceAccountUpn) || string.IsNullOrWhiteSpace(CallQueueName))
            {
                StatusMessage = "Error: Resource account UPN and call queue name cannot be empty";
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                ShowAssociateDialog = false;

                _loggingService.Log($"Associating resource account {ResourceAccountUpn} with call queue {CallQueueName}", LogLevel.Info);

                var command = _powerShellCommandService.GetAssociateResourceAccountWithCallQueueCommand(ResourceAccountUpn, CallQueueName);
                var result = await PreviewAndExecuteAsync(command, "Associate Resource Account");
                
                if (result == null)
                {
                    StatusMessage = "Operation cancelled by user";
                    return;
                }
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = $"Successfully associated resource account '{ResourceAccountUpn}' with call queue '{CallQueueName}'";
                    _loggingService.Log($"Successfully associated resource account {ResourceAccountUpn} with call queue {CallQueueName}", LogLevel.Info);
                }
                else
                {
                    StatusMessage = $"Error associating resource account with call queue: {result.Value}";
                    _loggingService.Log($"Error associating resource account {ResourceAccountUpn} with call queue {CallQueueName}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in AssociateResourceAccountWithCallQueueAsync: {ex}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RemoveCallQueueAsync(string? callQueueName = null)
        {
            var name = callQueueName ?? CallQueueName;
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusMessage = "Error: Call queue name cannot be empty";
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveCallQueueCommand(name);
                var result = await ConfirmAndExecuteAsync(command,
                    GetText(
                        UiTextKey.CallQueuesDeleteCallQueueConfirm,
                        "This permanently deletes the call queue '{name}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["name"] = name }),
                    "Delete Call Queue");

                if (result == null)
                {
                    StatusMessage = "Operation cancelled by user";
                    return;
                }

                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesDeleteCallQueueSuccess,
                        "Call queue '{name}' deleted successfully.",
                        new Dictionary<string, object?> { ["name"] = name });
                    _loggingService.Log($"Call queue {name} removed successfully", LogLevel.Info);
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveCallQueuesAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesDeleteCallQueueError,
                        "Error deleting the call queue: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    _loggingService.Log($"Error removing call queue {name}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                _loggingService.Log($"Exception in RemoveCallQueueAsync: {ex}", LogLevel.Error);
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
                StatusMessage = "Error: Resource account UPN cannot be empty";
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveResourceAccountCommand(accountUpn);
                var result = await ConfirmAndExecuteAsync(command,
                    $"This will permanently remove the resource account '{accountUpn}'. This action cannot be undone.",
                    "Remove Resource Account");

                if (result == null)
                {
                    StatusMessage = "Operation cancelled by user";
                    return;
                }

                if (result.HasSuccessMarker)
                {
                    StatusMessage = $"Resource account '{accountUpn}' removed successfully";
                    _loggingService.Log($"Resource account {accountUpn} removed successfully", LogLevel.Info);
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveResourceAccountsAsync();
                }
                else
                {
                    StatusMessage = $"Error removing resource account: {result.Value}";
                    _loggingService.Log($"Error removing resource account {accountUpn}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in RemoveResourceAccountAsync: {ex}", LogLevel.Error);
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

        private void ParseCallQueuesFromResult(string result)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.StartsWith("CALLQUEUE:") && line.Length > 10)
                {
                    var queueData = line.Substring(10).Split('|');
                    if (queueData.Length >= 4)
                    {
                        var queue = new CallQueue(
                            queueData[0].Trim(),
                            queueData[1].Trim(),
                            queueData[2].Trim(),
                            int.TryParse(queueData[3].Trim(), out var alertTime) ? alertTime : 0
                        );
                        CallQueues.Add(queue);
                    }
                }
            }
            OnPropertyChanged(nameof(CallQueuesView));
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

        private bool FilterCallQueue(object obj)
        {
            if (obj is not CallQueue queue)
                return false;

            if (string.IsNullOrWhiteSpace(SearchCallQueuesText))
                return true;

            var query = SearchCallQueuesText.Trim();
            return (queue.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (queue.Identity?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (queue.RoutingMethod?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        partial void OnSearchResourceAccountsTextChanged(string value)
        {
            OnPropertyChanged(nameof(ResourceAccountsView));
        }

        partial void OnSearchCallQueuesTextChanged(string value)
        {
            OnPropertyChanged(nameof(CallQueuesView));
        }
    }
} 
