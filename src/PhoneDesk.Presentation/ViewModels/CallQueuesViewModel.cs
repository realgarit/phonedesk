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
            LogLocalized(
                UiTextKey.CallQueuesPageLoadedLog,
                "Call queues page loaded",
                LogLevel.Info);

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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesLoadResourceAccountsStatus,
                    "Loading resource accounts...");

                LogLocalized(
                    UiTextKey.CallQueuesLoadResourceAccountsLog,
                    "Loading resource accounts starting with '{prefix}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["prefix"] = "racq-" });

                var command = _powerShellCommandService.GetRetrieveResourceAccountsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveResourceAccounts", allowThrottleRetry: true);
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseResourceAccountsFromResult(result.Value);
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesLoadResourceAccountsSuccess,
                        "Loaded {count} resource accounts starting with '{prefix}'.",
                        new Dictionary<string, object?>
                        {
                            ["count"] = ResourceAccounts.Count,
                            ["prefix"] = "racq-"
                        });
                    LogLocalized(
                        UiTextKey.CallQueuesLoadResourceAccountsSuccess,
                        "Loaded {count} resource accounts starting with '{prefix}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["count"] = ResourceAccounts.Count,
                            ["prefix"] = "racq-"
                        });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.CallQueuesLoadResourceAccountsNoOutputLog,
                        "Error loading resource accounts: no output from the PowerShell command.",
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
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RetrieveResourceAccountsAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesLoadCallQueuesStatus,
                    "Loading call queues...");

                LogLocalized(
                    UiTextKey.CallQueuesLoadCallQueuesLog,
                    "Loading call queues containing '{pattern}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["pattern"] = "cq-" });

                var command = _powerShellCommandService.GetRetrieveCallQueuesCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveCallQueues", allowThrottleRetry: true);
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseCallQueuesFromResult(result.Value);
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesLoadCallQueuesSuccess,
                        "Loaded {count} call queues containing '{pattern}'.",
                        new Dictionary<string, object?>
                        {
                            ["count"] = CallQueues.Count,
                            ["pattern"] = "cq-"
                        });
                    LogLocalized(
                        UiTextKey.CallQueuesLoadCallQueuesSuccess,
                        "Loaded {count} call queues containing '{pattern}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["count"] = CallQueues.Count,
                            ["pattern"] = "cq-"
                        });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.CallQueuesLoadCallQueuesNoOutputLog,
                        "Error loading call queues: no output from the PowerShell command.",
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
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RetrieveCallQueuesAsync),
                        ["details"] = ex.ToString()
                    });
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
                    LogLocalized(
                        UiTextKey.CallQueuesAssignLicenseErrorLog,
                        "Error assigning the license to '{upn}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = variables.RacqUPN,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(AssignLicenseAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.RuntimeVariablesNotFoundError,
                    "Error: Configuration not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(variables.M365Group))
            {
                StatusMessage = GetText(
                    UiTextKey.CallQueuesM365GroupNameMissingError,
                    "Error: M365 Group name is not set. Set Customer and Customer Group Name in Configuration first.");
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                StatusMessage = GetText(
                    UiTextKey.CallQueuesLoadM365GroupIdStatus,
                    "Loading M365 Group ID...");

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
                            StatusMessage = GetText(
                                UiTextKey.CallQueuesLoadM365GroupIdSuccess,
                                "M365 Group ID loaded and saved: {groupId}",
                                new Dictionary<string, object?> { ["groupId"] = groupId });
                            LogLocalized(
                                UiTextKey.CallQueuesLoadM365GroupIdLog,
                                "M365 Group ID saved: {groupId}",
                                LogLevel.Info,
                                new Dictionary<string, object?> { ["groupId"] = groupId });
                            return;
                        }
                    }
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesLoadM365GroupIdParseError,
                        "Error: Could not parse the M365 Group ID from the result.");
                    LogLocalized(
                        UiTextKey.CallQueuesLoadM365GroupIdParseErrorLog,
                        "Could not parse the M365 Group ID from the result. No M365GROUPID line was found.",
                        LogLevel.Error);
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesLoadM365GroupIdError,
                        "Error loading the M365 Group ID: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesLoadM365GroupIdErrorLog,
                        "Error loading the M365 Group ID: {details}",
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
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(GetM365GroupIdAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesCreateResourceAccountValidationError,
                    "Error: Resource account UPN and display name cannot be empty.");
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
                    StatusMessage = GetText(
                        UiTextKey.RuntimeVariablesNotFoundError,
                        "Error: Configuration not found.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(variables.CsAppCqId))
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesApplicationIdMissingError,
                        "Error: Call queue application ID was not found in Configuration.");
                    return;
                }

                // Validate that the UPN includes a domain
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

                var command = _powerShellCommandService.GetCreateResourceAccountCommand(upn, ResourceAccountDisplayName, variables.CsAppCqId);
                LogLocalized(
                    UiTextKey.CallQueuesCreateResourceAccountLog,
                    "Creating resource account '{upn}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["upn"] = upn });
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.CallQueuesCreateResourceAccountContext,
                        "Create resource account"));
                
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
                        UiTextKey.CallQueuesCreateResourceAccountSuccess,
                        "Resource account '{upn}' created successfully.",
                        new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });
                    LogLocalized(
                        UiTextKey.CallQueuesCreateResourceAccountSuccess,
                        "Resource account '{upn}' created successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });
                    // Don't auto-refresh to avoid showing "Found ... resource accounts" message
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesCreateResourceAccountError,
                        "Error creating the resource account: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesCreateResourceAccountErrorLog,
                        "Error creating the resource account '{upn}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(CreateResourceAccountAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesUpdateUsageLocationUpnRequiredError,
                    "Error: Resource account UPN cannot be empty.");
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
                    StatusMessage = GetText(
                        UiTextKey.RuntimeVariablesNotFoundError,
                        "Error: Configuration not found.");
                    return;
                }

                LogLocalized(
                    UiTextKey.CallQueuesUpdateUsageLocationLog,
                    "Updating the usage location for '{upn}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });

                var command = _powerShellCommandService.GetUpdateResourceAccountUsageLocationCommand(ResourceAccountUpn, variables.UsageLocation);
                var result = await ExecutePowerShellCommandAsync(
                    command,
                    GetText(
                        UiTextKey.CallQueuesUpdateUsageLocationContext,
                        "Update usage location"));
                
                if (result.HasSuccessMarker)
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesUpdateUsageLocationSuccess,
                        "Usage location updated for '{upn}' to '{usageLocation}'.",
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["usageLocation"] = variables.UsageLocation
                        });
                    LogLocalized(
                        UiTextKey.CallQueuesUpdateUsageLocationSuccessLog,
                        "Usage location updated for '{upn}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = ResourceAccountUpn });
                    // Don't auto-refresh to avoid showing "Found ... resource accounts" message
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesUpdateUsageLocationError,
                        "Error updating the usage location: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesUpdateUsageLocationErrorLog,
                        "Error updating the usage location for '{upn}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(UpdateUsageLocationAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesNameRequiredError,
                    "Error: Call queue name cannot be empty.");
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
                    StatusMessage = GetText(
                        UiTextKey.RuntimeVariablesNotFoundError,
                        "Error: Configuration not found.");
                    return;
                }

                LogLocalized(
                    UiTextKey.CallQueuesCreateCallQueueLog,
                    "Creating call queue '{name}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["name"] = CallQueueName });

                var command = _powerShellCommandService.GetCreateCallQueueCommand(CallQueueName, variables.LanguageId, variables.M365GroupId, variables);
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.CallQueuesCreateCallQueueContext,
                        "Create call queue"));
                
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
                        UiTextKey.CallQueuesCreateCallQueueSuccess,
                        "Call queue '{name}' created successfully.",
                        new Dictionary<string, object?> { ["name"] = CallQueueName });
                    LogLocalized(
                        UiTextKey.CallQueuesCreateCallQueueSuccessLog,
                        "Call queue '{name}' created successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = CallQueueName });
                    // Don't auto-refresh to avoid showing "Found ... call queues" message
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesCreateCallQueueError,
                        "Error creating the call queue: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesCreateCallQueueErrorLog,
                        "Error creating the call queue '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["name"] = CallQueueName,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(CreateCallQueueAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesAssociateDetailsRequiredError,
                    "Error: Resource account UPN and call queue name cannot be empty.");
                return;
            }

            try
            {
                WaitingMessage = ConstantsService.Messages.WaitingMessage;
                IsBusy = true;
                ShowAssociateDialog = false;

                LogLocalized(
                    UiTextKey.CallQueuesAssociateResourceAccountLog,
                    "Associating resource account '{upn}' with call queue '{name}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?>
                    {
                        ["upn"] = ResourceAccountUpn,
                        ["name"] = CallQueueName
                    });

                var command = _powerShellCommandService.GetAssociateResourceAccountWithCallQueueCommand(ResourceAccountUpn, CallQueueName);
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.CallQueuesAssociateResourceAccountContext,
                        "Associate resource account"));
                
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
                        UiTextKey.CallQueuesAssociateResourceAccountSuccess,
                        "Resource account '{upn}' was associated with call queue '{name}' successfully.",
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["name"] = CallQueueName
                        });
                    LogLocalized(
                        UiTextKey.CallQueuesAssociateResourceAccountSuccessLog,
                        "Resource account '{upn}' was associated with call queue '{name}' successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["name"] = CallQueueName
                        });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesAssociateResourceAccountError,
                        "Error associating the resource account with the call queue: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesAssociateResourceAccountErrorLog,
                        "Error associating resource account '{upn}' with call queue '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = ResourceAccountUpn,
                            ["name"] = CallQueueName,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(AssociateResourceAccountWithCallQueueAsync),
                        ["details"] = ex.ToString()
                    });
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
                StatusMessage = GetText(
                    UiTextKey.CallQueuesNameRequiredError,
                    "Error: Call queue name cannot be empty.");
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
                    GetText(
                        UiTextKey.CallQueuesDeleteCallQueueContext,
                        "Delete call queue"));

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
                        UiTextKey.CallQueuesDeleteCallQueueSuccess,
                        "Call queue '{name}' deleted successfully.",
                        new Dictionary<string, object?> { ["name"] = name });
                    LogLocalized(
                        UiTextKey.CallQueuesDeleteCallQueueLog,
                        "Call queue '{name}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = name });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveCallQueuesAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesDeleteCallQueueError,
                        "Error deleting the call queue: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesDeleteCallQueueErrorLog,
                        "Error deleting the call queue '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RemoveCallQueueAsync),
                        ["details"] = ex.ToString()
                    });
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
                    UiTextKey.CallQueuesUpdateUsageLocationUpnRequiredError,
                    "Error: Resource account UPN cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveResourceAccountCommand(accountUpn);
                var result = await ConfirmAndExecuteAsync(command,
                    GetText(
                        UiTextKey.CallQueuesDeleteResourceAccountConfirm,
                        "This permanently deletes the resource account '{upn}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["upn"] = accountUpn }),
                    GetText(
                        UiTextKey.CallQueuesDeleteResourceAccountContext,
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
                        UiTextKey.CallQueuesDeleteResourceAccountSuccess,
                        "Resource account '{upn}' deleted successfully.",
                        new Dictionary<string, object?> { ["upn"] = accountUpn });
                    LogLocalized(
                        UiTextKey.CallQueuesDeleteResourceAccountLog,
                        "Resource account '{upn}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["upn"] = accountUpn });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveResourceAccountsAsync();
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.CallQueuesDeleteResourceAccountError,
                        "Error deleting the resource account: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.CallQueuesDeleteResourceAccountErrorLog,
                        "Error deleting the resource account '{upn}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["upn"] = accountUpn,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RemoveResourceAccountAsync),
                        ["details"] = ex.ToString()
                    });
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
