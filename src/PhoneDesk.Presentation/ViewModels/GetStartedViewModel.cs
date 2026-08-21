using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using System.Threading.Tasks;
using System;

namespace PhoneDesk.ViewModels
{
    public partial class GetStartedViewModel : ViewModelBase
    {
        [ObservableProperty]
        private bool _modulesChecked;

        [ObservableProperty]
        private bool _teamsConnected;

        [ObservableProperty]
        private bool _graphConnected;

        [ObservableProperty]
        private bool _canProceed;

        private string? _lastSetupError;

        public bool CanConnectTeams => ModulesChecked && !TeamsConnected;
        public bool CanConnectGraph => ModulesChecked && !GraphConnected;

        public string SetupGuidance { get; private set; } = string.Empty;
        public string SetupGuidanceDetail => CanProceed
            ? "Your connections are ready. You can start the customer configuration now."
            : "Setup becomes available only after all three checks pass.";

        public string ModulesActionText => ModulesChecked ? "Check again" : "Check modules";
        public string TeamsActionText => TeamsConnected
            ? "Connected"
            : ModulesChecked ? "Connect Teams" : "Check modules first";
        public string GraphActionText => GraphConnected
            ? "Connected"
            : ModulesChecked ? "Connect Graph" : "Check modules first";

        public string ModulesStatusText => $"Step 1 of 3: Modules — {(ModulesChecked ? "Ready" : "Not checked yet")}";
        public string TeamsStatusText => $"Step 2 of 3: Microsoft Teams — {(TeamsConnected ? "Connected" : "Waiting")}";
        public string GraphStatusText => $"Step 3 of 3: Microsoft Graph — {(GraphConnected ? "Connected" : "Waiting")}";

        private readonly IMsalGraphAuthenticationService _msalAuthService;

        public GetStartedViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            IMsalGraphAuthenticationService msalAuthService,
            IAuditLog? auditLog = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, auditLog: auditLog)
        {
            _msalAuthService = msalAuthService;
            _modulesChecked = _sessionManager.ModulesChecked;
            _teamsConnected = _sessionManager.TeamsConnected;
            _graphConnected = _sessionManager.GraphConnected;
            UpdateCanProceed();

            _loggingService.Log("Get Started page loaded", LogLevel.Info);
        }

        [RelayCommand]
        private void NavigateToPage(string page)
        {
            if (!CanProceed)
            {
                _loggingService.Log("Cannot navigate: prerequisites not met", LogLevel.Warning);
                return;
            }

            NavigateTo(page);
        }

        [RelayCommand]
        private async Task CheckModulesAsync()
        {
            try
            {
                _lastSetupError = null;
                IsBusy = true;
                _loggingService.Log("Checking PowerShell modules...", LogLevel.Info);

                var command = _powerShellCommandService.GetCheckModulesCommand();
                var result = await ExecutePowerShellCommandAsync(command, "CheckModules");
                var output = result.Value ?? string.Empty;

                // Check for Teams module
                bool teamsAvailable = output.Contains("MicrosoftTeams module is available") || output.Contains("MicrosoftTeams module installed successfully");

                // Check for all required Graph modules
                bool graphAuthAvailable = output.Contains(ConstantsService.PowerShellModules.MicrosoftGraphAuthentication + " module is available");
                bool graphUsersAvailable = output.Contains(ConstantsService.PowerShellModules.MicrosoftGraphUsers + " module is available");
                bool graphUsersActionsAvailable = output.Contains(ConstantsService.PowerShellModules.MicrosoftGraphUsersActions + " module is available");
                bool graphGroupsAvailable = output.Contains(ConstantsService.PowerShellModules.MicrosoftGraphGroups + " module is available");
                bool graphIdentityAvailable = output.Contains(ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement + " module is available");

                ModulesChecked = teamsAvailable &&
                                graphAuthAvailable &&
                                graphUsersAvailable &&
                                graphUsersActionsAvailable &&
                                graphGroupsAvailable &&
                                graphIdentityAvailable;

                _sessionManager.UpdateModulesChecked(ModulesChecked);

                if (ModulesChecked)
                {
                    _loggingService.Log("PowerShell modules are available", LogLevel.Success);
                    foreach (var line in output.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _loggingService.Log(line.Trim(), LogLevel.Info);
                        }
                    }
                }
                else
                {
                    var errorMessage = "One or more required modules could not be installed";
                    _loggingService.Log(errorMessage, LogLevel.Error);
                    SetSetupError("Some required PowerShell modules are missing. Review the log, then try again.");
                }
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error checking PowerShell modules: {ex.Message}", LogLevel.Error);
                ModulesChecked = false;
                _sessionManager.UpdateModulesChecked(false);
                SetSetupError("The module check could not be completed. Review the log and try again.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ConnectTeamsAsync()
        {
            try
            {
                if (!ModulesChecked)
                {
                    _loggingService.Log("Please check modules first", LogLevel.Warning);
                    SetSetupError("Check the required modules before connecting to Microsoft Teams.");
                    return;
                }

                _lastSetupError = null;
                IsBusy = true;
                _loggingService.Log("Connecting to Microsoft Teams...", LogLevel.Info);

                var command = _powerShellCommandService.GetConnectTeamsCommand();
                var result = await ExecutePowerShellCommandAsync(command, "ConnectTeams");
                TeamsConnected = result.HasSuccessMarker;

                string? tenantId = null;
                string? tenantName = null;

                if (TeamsConnected)
                {
                    _loggingService.Log("Connected to Microsoft Teams successfully", LogLevel.Success);

                    foreach (var line in (result.Value ?? string.Empty).Split('\n'))
                    {
                        if (line.Contains("Connected to tenant:"))
                        {
                            var parts = line.Split(':')[1].Trim().Split('(');
                            if (parts.Length >= 2)
                            {
                                tenantName = parts[0].Trim();
                                tenantId = parts[1].Trim(')').Trim();
                            }
                        }
                    }
                }
                else
                {
                    _loggingService.Log($"Error connecting to Microsoft Teams: {result.Value}", LogLevel.Error);
                    SetSetupError("Microsoft Teams could not be connected. Check the sign-in window and try again.");
                }

                _sessionManager.UpdateTeamsConnection(TeamsConnected);
                if (tenantId != null && tenantName != null)
                {
                    _sessionManager.UpdateTenantInfo(tenantId, tenantName);
                }

                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error connecting to Microsoft Teams: {ex.Message}", LogLevel.Error);
                TeamsConnected = false;
                _sessionManager.UpdateTeamsConnection(false);
                UpdateCanProceed();
                SetSetupError("Microsoft Teams could not be connected. Check the sign-in window and try again.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ConnectGraphAsync()
        {
            try
            {
                if (!ModulesChecked)
                {
                    _loggingService.Log("Please check modules first", LogLevel.Warning);
                    SetSetupError("Check the required modules before connecting to Microsoft Graph.");
                    return;
                }

                _lastSetupError = null;
                IsBusy = true;
                _loggingService.Log("Authenticating to Microsoft Graph...", LogLevel.Info);

                // Step 1: Authenticate using MSAL (opens browser popup)
                var authResult = await _msalAuthService.AuthenticateAsync();

                if (!authResult.Success || string.IsNullOrEmpty(authResult.AccessToken))
                {
                    _loggingService.Log($"Authentication failed: {authResult.ErrorMessage}", LogLevel.Error);
                    GraphConnected = false;
                    _sessionManager.UpdateGraphConnection(false);
                    UpdateCanProceed();
                    SetSetupError("Microsoft Graph sign-in did not complete. Finish the browser sign-in and try again.");
                    return;
                }

                _loggingService.Log("Authentication successful, connecting PowerShell to Microsoft Graph...", LogLevel.Info);

                // Step 2: Pass the token securely via environment variable (not embedded in script)
                // This prevents the token from appearing in logs or error messages
                var command = _powerShellCommandService.GetConnectGraphWithTokenCommand(authResult.AccessToken);
                var envVars = new Dictionary<string, string>
                {
                    { "TEAMS_PM_TOKEN", authResult.AccessToken }
                };
                var result = await ExecutePowerShellCommandAsync(command, envVars, "ConnectGraph");
                GraphConnected = result.HasSuccessMarker;

                string? account = authResult.Account;

                if (GraphConnected)
                {
                    _loggingService.Log($"Connected to Microsoft Graph successfully as {account}", LogLevel.Success);
                }
                else
                {
                    _loggingService.Log($"Error connecting to Microsoft Graph: {result.Value}", LogLevel.Error);
                    SetSetupError("Microsoft Graph could not be connected. Finish the browser sign-in and try again.");
                }

                _sessionManager.UpdateGraphConnection(GraphConnected, account);
                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error connecting to Microsoft Graph: {ex.Message}", LogLevel.Error);
                GraphConnected = false;
                _sessionManager.UpdateGraphConnection(false);
                UpdateCanProceed();
                SetSetupError("Microsoft Graph could not be connected. Finish the browser sign-in and try again.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task DisconnectTeamsAsync()
        {
            try
            {
                _lastSetupError = null;
                IsBusy = true;
                _loggingService.Log("Disconnecting from Microsoft Teams...", LogLevel.Info);

                var command = _powerShellCommandService.GetDisconnectTeamsCommand();
                var result = await ExecutePowerShellCommandAsync(command, "DisconnectTeams");
                TeamsConnected = false;
                _sessionManager.UpdateTeamsConnection(false);
                _loggingService.Log("Disconnected from Microsoft Teams", LogLevel.Info);
                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error disconnecting from Microsoft Teams: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task DisconnectGraphAsync()
        {
            try
            {
                _lastSetupError = null;
                IsBusy = true;
                _loggingService.Log("Disconnecting from Microsoft Graph...", LogLevel.Info);

                // Sign out from MSAL to clear cached tokens
                await _msalAuthService.SignOutAsync();

                var command = _powerShellCommandService.GetDisconnectGraphCommand();
                var result = await ExecutePowerShellCommandAsync(command, "DisconnectGraph");
                GraphConnected = false;
                _sessionManager.UpdateGraphConnection(false);
                _loggingService.Log("Disconnected from Microsoft Graph", LogLevel.Info);
                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error disconnecting from Microsoft Graph: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateCanProceed()
        {
            CanProceed = ModulesChecked && TeamsConnected && GraphConnected;
            UpdateGuidance();
        }

        private void SetSetupError(string message)
        {
            _lastSetupError = message;
            UpdateGuidance();
        }

        private void UpdateGuidance()
        {
            SetupGuidance = CanProceed
                ? "Everything is ready. Start configuration when you're ready."
                : _lastSetupError
                    ?? (!ModulesChecked
                        ? "Start here: check the bundled modules before connecting to a customer tenant."
                        : !TeamsConnected
                            ? "The modules are ready. Connect to Microsoft Teams next."
                            : "Teams is connected. Connect to Microsoft Graph next; a browser sign-in window will open.");

            OnPropertyChanged(nameof(SetupGuidance));
            OnPropertyChanged(nameof(SetupGuidanceDetail));
            OnPropertyChanged(nameof(ModulesActionText));
            OnPropertyChanged(nameof(TeamsActionText));
            OnPropertyChanged(nameof(GraphActionText));
        }

        partial void OnModulesCheckedChanged(bool value)
        {
            UpdateCanProceed();
            OnPropertyChanged(nameof(CanConnectTeams));
            OnPropertyChanged(nameof(CanConnectGraph));
            OnPropertyChanged(nameof(ModulesStatusText));
            OnPropertyChanged(nameof(ModulesActionText));
        }

        partial void OnTeamsConnectedChanged(bool value)
        {
            UpdateCanProceed();
            OnPropertyChanged(nameof(CanConnectTeams));
            OnPropertyChanged(nameof(TeamsStatusText));
            OnPropertyChanged(nameof(TeamsActionText));
        }

        partial void OnGraphConnectedChanged(bool value)
        {
            UpdateCanProceed();
            OnPropertyChanged(nameof(CanConnectGraph));
            OnPropertyChanged(nameof(GraphStatusText));
            OnPropertyChanged(nameof(GraphActionText));
        }
    }
}
