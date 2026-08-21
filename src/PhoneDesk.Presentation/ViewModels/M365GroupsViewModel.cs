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
            _loggingService.Log("M365 Groups page loaded", LogLevel.Info);

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
                GroupStatus = "Retrieving M365 groups...";

                _loggingService.Log("Retrieving M365 groups starting with 'ttgrp'", LogLevel.Info);

                var command = _powerShellCommandService.GetRetrieveM365GroupsCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveM365Groups", allowThrottleRetry: true);

                if (!string.IsNullOrEmpty(result.Value))
                {
                    ParseGroupsFromResult(result.Value);
                    GroupStatus = $"Found {Groups.Count} groups starting with 'ttgrp'";
                    _loggingService.Log($"Retrieved {Groups.Count} M365 groups", LogLevel.Info);
                }
                else
                {
                    GroupStatus = "Error: No output from PowerShell command";
                    _loggingService.Log("Error retrieving M365 groups: No output from PowerShell command", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                GroupStatus = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in RetrieveM365GroupsAsync: {ex}", LogLevel.Error);
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
                GroupStatus = "Error: Group name cannot be empty";
                return;
            }

            try
            {
                IsBusy = true;
                ShowCreateGroupDialog = false;

                _loggingService.Log($"Creating new M365 group: {NewGroupName}", LogLevel.Info);

                var command = _powerShellCommandService.GetCreateM365GroupCommand(NewGroupName);
                var result = await PreviewAndExecuteAsync(command, "Create M365 Group");
                
                if (result == null)
                {
                    GroupStatus = "Operation cancelled by user";
                    return;
                }
                
                if (!string.IsNullOrEmpty(result.Value))
                {
                    var output = result.Value.Trim();
                    if (output.Contains("created successfully"))
                    {
                        GroupStatus = $"Group '{NewGroupName}' created successfully";
                        _loggingService.Log($"M365 Group {NewGroupName} created successfully", LogLevel.Info);
                        
                        // Refresh the groups list
                        if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                            await RetrieveM365GroupsAsync();
                    }
                    else if (output.Contains("already exists"))
                    {
                        GroupStatus = $"Group '{NewGroupName}' already exists";
                        _loggingService.Log($"M365 Group {NewGroupName} already exists", LogLevel.Warning);
                    }
                    else
                    {
                        GroupStatus = $"Error creating group: {output}";
                        _loggingService.Log($"Error creating M365 Group {NewGroupName}: {output}", LogLevel.Error);
                    }
                }
                else
                {
                    GroupStatus = "Error: No output from PowerShell command";
                    _loggingService.Log($"Error creating M365 Group {NewGroupName}: No output from PowerShell command", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                GroupStatus = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in CreateNewGroupAsync for group {NewGroupName}: {ex}", LogLevel.Error);
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
                GroupStatus = "Error: Please select a group to remove";
                return;
            }

            try
            {
                IsBusy = true;

                var command = _powerShellCommandService.GetRemoveM365GroupCommand(SelectedGroup.Id, SelectedGroup.DisplayName);
                var result = await ConfirmAndExecuteAsync(command,
                    $"This will permanently remove the M365 Group '{SelectedGroup.DisplayName}'. This action cannot be undone.",
                    "Remove M365 Group");

                if (result == null)
                {
                    GroupStatus = "Operation cancelled by user";
                    return;
                }

                if (result.HasSuccessMarker)
                {
                    GroupStatus = $"Group '{SelectedGroup.DisplayName}' removed successfully";
                    _loggingService.Log($"M365 Group {SelectedGroup.DisplayName} removed successfully", LogLevel.Info);
                    if (_sharedStateService?.AutoRefreshAfterOperations ?? true)
                        await RetrieveM365GroupsAsync();
                }
                else
                {
                    GroupStatus = $"Error removing group: {result.Value}";
                    _loggingService.Log($"Error removing M365 Group {SelectedGroup.DisplayName}: {result.Value}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                GroupStatus = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in RemoveM365GroupAsync: {ex}", LogLevel.Error);
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
                    _loggingService.Log("Variables not found. Please set variables first.", LogLevel.Error);
                    GroupStatus = "Error: Variables not found. Please set variables first.";
                    return;
                }

                if (!await ValidateVariables(variables))
                {
                    return;
                }

                m365group = variables.M365Group;
                _loggingService.Log($"Checking M365 group: {m365group}", LogLevel.Info);

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
                        _loggingService.Log($"Could not parse group ID from output: {output}", LogLevel.Warning);
                    }
                    GroupStatus = output.Contains("already exists") ? "Group already exists" : "Group created successfully";
                    IsGroupChecked = true;
                    _loggingService.Log($"M365 Group {m365group} check completed: {GroupStatus}", LogLevel.Info);
                }
                else
                {
                    GroupStatus = "Error: No output from PowerShell command";
                    _loggingService.Log($"Error checking M365 Group {m365group}: No output from PowerShell command", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                GroupStatus = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in CheckM365GroupAsync for group {m365group}: {ex}", LogLevel.Error);
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
