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
    public partial class M365GroupsViewModel : ViewModelBase
    {

        [ObservableProperty]
        private bool _isGroupChecked;

        [ObservableProperty]
        private string _groupId = string.Empty;

        [ObservableProperty]
        private string _groupStatus = string.Empty;

        [ObservableProperty]
        private bool _showConfirmation;

        [ObservableProperty]
        private ObservableCollection<M365Group> _groups = new();

        [ObservableProperty]
        private M365Group? _selectedGroup;

        [ObservableProperty]
        private string _newGroupName = string.Empty;

        [ObservableProperty]
        private string _newGroupDescription = string.Empty;

        [ObservableProperty]
        private bool _showCreateGroupDialog;

        [ObservableProperty]
        private string _searchText = string.Empty;

        public ObservableCollection<M365Group> GroupsView => new ObservableCollection<M365Group>(
            Groups.Where(FilterGroup)
        );

        public M365GroupsViewModel(
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
                UiTextKey.M365GroupsPageLoadedLog,
                "M365 Groups page loaded",
                LogLevel.Info);

            // Initialize with auto-generated group name if variables are available
            UpdateNewGroupName();

            Groups.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(GroupsView));
            };
        }

        [RelayCommand]
        private void ShowVariablesConfirmation()
        {
            ShowConfirmation = true;
        }

        [RelayCommand]
        private void NavigateToVariablesPage()
        {
            NavigateToVariables();
        }

        [RelayCommand]
        private async Task RetrieveM365GroupsAsync()
        {
            try
            {
                IsBusy = true;
                Groups.Clear();
                GroupStatus = GetText(
                    UiTextKey.M365GroupsLoadGroupsStatus,
                    "Loading M365 groups...");

                LogLocalized(
                    UiTextKey.M365GroupsLoadGroupsLog,
                    "Loading M365 groups starting with '{prefix}'",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["prefix"] = "ttgrp" });

                var command = _powerShellCommandService.GetRetrieveM365GroupsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveM365Groups", allowThrottleRetry: true);

                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseGroupsFromResult(result.Value);
                    GroupStatus = GetText(
                        UiTextKey.M365GroupsLoadGroupsSuccess,
                        "Loaded {count} groups starting with '{prefix}'.",
                        new Dictionary<string, object?>
                        {
                            ["count"] = Groups.Count,
                            ["prefix"] = "ttgrp"
                        });
                    LogLocalized(
                        UiTextKey.M365GroupsLoadGroupsSuccess,
                        "Loaded {count} groups starting with '{prefix}'.",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["count"] = Groups.Count,
                            ["prefix"] = "ttgrp"
                        });
                }
                else
                {
                    GroupStatus = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.M365GroupsLoadGroupsNoOutputLog,
                        "Error loading M365 groups: no output from the PowerShell command.",
                        LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                GroupStatus = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RetrieveM365GroupsAsync),
                        ["details"] = ex.ToString()
                    });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void OpenCreateGroupDialog()
        {
            UpdateNewGroupName();
            ShowCreateGroupDialog = true;
        }

        [RelayCommand]
        private void CloseCreateGroupDialog()
        {
            ShowCreateGroupDialog = false;
        }

        [RelayCommand]
        private async Task CreateNewGroupAsync()
        {
            if (string.IsNullOrWhiteSpace(NewGroupName))
            {
                GroupStatus = GetText(
                    UiTextKey.M365GroupsNameRequiredError,
                    "Error: Group name cannot be empty.");
                return;
            }

            try
            {
                IsBusy = true;
                ShowCreateGroupDialog = false;

                LogLocalized(
                    UiTextKey.M365GroupsCreateGroupLog,
                    "Creating M365 group '{name}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["name"] = NewGroupName });

                var command = _powerShellCommandService.GetCreateM365GroupCommand(NewGroupName);
                var result = await PreviewAndExecuteAsync(
                    command,
                    GetText(
                        UiTextKey.M365GroupsCreateGroupContext,
                        "Create M365 group"));
                
                if (result == null)
                {
                    GroupStatus = GetText(
                        UiTextKey.RuntimeOperationCancelledByUser,
                        "Operation cancelled by user");
                    return;
                }
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    var output = result.Value.Trim();
                    if (output.Contains("created successfully"))
                    {
                        GroupStatus = GetText(
                            UiTextKey.M365GroupsCreateGroupSuccess,
                            "Group '{name}' created successfully.",
                            new Dictionary<string, object?> { ["name"] = NewGroupName });
                        LogLocalized(
                            UiTextKey.M365GroupsCreateGroupSuccessLog,
                            "M365 group '{name}' created successfully.",
                            LogLevel.Info,
                            new Dictionary<string, object?> { ["name"] = NewGroupName });
                        
                        // Refresh the groups list
                        if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                            await RetrieveM365GroupsAsync();
                    }
                    else if (output.Contains("already exists"))
                    {
                        GroupStatus = GetText(
                            UiTextKey.M365GroupsCreateGroupAlreadyExists,
                            "Group '{name}' already exists.",
                            new Dictionary<string, object?> { ["name"] = NewGroupName });
                        LogLocalized(
                            UiTextKey.M365GroupsCreateGroupAlreadyExistsLog,
                            "M365 group '{name}' already exists.",
                            LogLevel.Warning,
                            new Dictionary<string, object?> { ["name"] = NewGroupName });
                    }
                    else
                    {
                        GroupStatus = GetText(
                            UiTextKey.M365GroupsCreateGroupError,
                            "Error creating the group: {details}",
                            new Dictionary<string, object?> { ["details"] = output });
                        LogLocalized(
                            UiTextKey.M365GroupsCreateGroupErrorLog,
                            "Error creating M365 group '{name}': {details}",
                            LogLevel.Error,
                            new Dictionary<string, object?>
                            {
                                ["name"] = NewGroupName,
                                ["details"] = output
                            });
                    }
                }
                else
                {
                    GroupStatus = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.M365GroupsCreateGroupNoOutputLog,
                        "Error creating M365 group '{name}': no output from the PowerShell command.",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["name"] = NewGroupName });
                }
            }
            catch (Exception ex)
            {
                GroupStatus = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(CreateNewGroupAsync),
                        ["details"] = ex.ToString()
                    });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RemoveM365GroupAsync()
        {
            if (SelectedGroup == null)
            {
                GroupStatus = GetText(
                    UiTextKey.M365GroupsDeleteSelectionRequiredError,
                    "Error: Select a group to delete.");
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveM365GroupCommand(SelectedGroup.Id, SelectedGroup.DisplayName);
                var result = await ConfirmAndExecuteAsync(command,
                    GetText(
                        UiTextKey.M365GroupsDeleteGroupConfirm,
                        "This permanently deletes the M365 group '{name}'. This action cannot be undone.",
                        new Dictionary<string, object?> { ["name"] = SelectedGroup.DisplayName }),
                    GetText(
                        UiTextKey.M365GroupsDeleteGroupContext,
                        "Delete M365 group"));

                if (result == null)
                {
                    GroupStatus = GetText(
                        UiTextKey.RuntimeOperationCancelledByUser,
                        "Operation cancelled by user");
                    return;
                }

                if (result.HasSuccessMarker)
                {
                    GroupStatus = GetText(
                        UiTextKey.M365GroupsDeleteGroupSuccess,
                        "Group '{name}' deleted successfully.",
                        new Dictionary<string, object?> { ["name"] = SelectedGroup.DisplayName });
                    LogLocalized(
                        UiTextKey.M365GroupsDeleteGroupSuccessLog,
                        "M365 group '{name}' deleted successfully.",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = SelectedGroup.DisplayName });
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveM365GroupsAsync();
                }
                else
                {
                    GroupStatus = GetText(
                        UiTextKey.M365GroupsDeleteGroupError,
                        "Error deleting the group: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    LogLocalized(
                        UiTextKey.M365GroupsDeleteGroupErrorLog,
                        "Error deleting M365 group '{name}': {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?>
                        {
                            ["name"] = SelectedGroup.DisplayName,
                            ["details"] = result.Value
                        });
                }
            }
            catch (Exception ex)
            {
                GroupStatus = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(RemoveM365GroupAsync),
                        ["details"] = ex.ToString()
                    });
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task CheckM365GroupAsync()
        {
            string m365group = string.Empty;
            try
            {
                IsBusy = true;
                ShowConfirmation = false;

                var variables = _sharedStateService?.Variables;
                if (variables == null)
                {
                    LogLocalized(
                        UiTextKey.M365GroupsCheckVariablesMissingLog,
                        "Configuration was not found. Set the configuration first.",
                        LogLevel.Error);
                    GroupStatus = GetText(
                        UiTextKey.M365GroupsCheckVariablesMissingError,
                        "Error: Configuration not found. Set the configuration first.");
                    return;
                }

                if (!await ValidateVariables(variables))
                {
                    return;
                }

                m365group = variables.M365Group;
                LogLocalized(
                    UiTextKey.M365GroupsCheckGroupLog,
                    "Checking M365 group '{name}'.",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["name"] = m365group });

                var command = _powerShellCommandService.GetCreateM365GroupCommand(m365group);
                var result = await ExecutePowerShellCommandAsync(command, "CheckM365Group");
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    var output = result.Value.Trim();
                    var parts = output.Split(':');
                    if (parts.Length >= 2)
                    {
                        GroupId = parts[1].Trim();
                    }
                    else
                    {
                        GroupId = string.Empty;
                        LogLocalized(
                            UiTextKey.M365GroupsCheckGroupParseWarningLog,
                            "Could not parse the group ID from the output: {details}",
                            LogLevel.Warning,
                            new Dictionary<string, object?> { ["details"] = output });
                    }
                    GroupStatus = output.Contains("already exists")
                        ? GetText(
                            UiTextKey.M365GroupsCheckGroupExistsStatus,
                            "Group already exists.")
                        : GetText(
                            UiTextKey.M365GroupsCheckGroupCreatedStatus,
                            "Group created successfully.");
                    IsGroupChecked = true;
                    LogLocalized(
                        UiTextKey.M365GroupsCheckGroupCompletedLog,
                        "M365 group '{name}' check completed: {status}",
                        LogLevel.Info,
                        new Dictionary<string, object?>
                        {
                            ["name"] = m365group,
                            ["status"] = GroupStatus
                        });
                }
                else
                {
                    GroupStatus = GetText(
                        UiTextKey.RuntimeNoPowerShellOutputError,
                        "Error: No output from the PowerShell command.");
                    LogLocalized(
                        UiTextKey.M365GroupsCheckGroupNoOutputLog,
                        "Error checking M365 group '{name}': no output from the PowerShell command.",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["name"] = m365group });
                }
            }
            catch (Exception ex)
            {
                GroupStatus = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?>
                    {
                        ["context"] = nameof(CheckM365GroupAsync),
                        ["details"] = ex.ToString()
                    });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ParseGroupsFromResult(string result)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.StartsWith("GROUP:"))
                {
                    var groupData = line.Substring(6).Split('|');
                    if (groupData.Length >= 4)
                    {
                        var group = new M365Group(
                            groupData[0].Trim(),
                            groupData[1].Trim(),
                            groupData[2].Trim(),
                            groupData[3].Trim()
                        );
                        Groups.Add(group);
                    }
                }
            }
            OnPropertyChanged(nameof(GroupsView));
        }

        private void UpdateNewGroupName()
        {
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer) && !string.IsNullOrEmpty(variables.CustomerGroupName))
            {
                NewGroupName = variables.M365Group;
            }
            else
            {
                NewGroupName = "ttgrp-";
            }
        }

        private bool FilterGroup(object obj)
        {
            if (obj is not M365Group group)
                return false;

            if (string.IsNullOrWhiteSpace(SearchText))
                return true;

            var query = SearchText.Trim();
            return (group.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (group.MailNickname?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (group.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (group.Id?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(GroupsView));
        }
    }
} 
