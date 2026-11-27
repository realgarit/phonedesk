using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Localization;
using System.Threading.Tasks;
using System;
using System.ComponentModel;

namespace PhoneDesk.ViewModels
{
    public partial class GetStartedViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;

        [ObservableProperty]
        private bool _modulesChecked;

        [ObservableProperty]
        private bool _teamsConnected;

        [ObservableProperty]
        private bool _graphConnected;

        [ObservableProperty]
        private bool _canProceed;

        /// <summary>The tenant the operator means: its ID, one of its domains or its name. Optional.</summary>
        [ObservableProperty]
        private string _expectedTenant = string.Empty;

        /// <summary>True while the Microsoft Graph sign-in window is open and the page waits for it.</summary>
        [ObservableProperty]
        private bool _graphSignInPending;

        private readonly PhoneDesk.Operations.TenantIdentityGuard _identityGuard;
        private TaskCompletionSource<bool>? _abandonGraphSignIn;

        /// <summary>Who the connections are signed in as, read from the tenant after a sign-in.</summary>
        public string IdentityText
        {
            get
            {
                var identity = _identityGuard.Identity;
                if (identity is null || (!identity.TeamsKnown && !identity.GraphKnown))
                {
                    return string.Empty;
                }

                var parts = new List<string>();
                if (identity.TeamsKnown)
                {
                    parts.Add(GetText(
                        UiTextKey.GetStartedIdentityTeams,
                        "Microsoft Teams: {name} ({id})",
                        new Dictionary<string, object?> { ["name"] = identity.TeamsTenantName, ["id"] = identity.TeamsTenantId }));
                }

                if (identity.GraphKnown)
                {
                    parts.Add(GetText(
                        UiTextKey.GetStartedIdentityGraph,
                        "Microsoft Graph: {account} ({id})",
                        new Dictionary<string, object?> { ["account"] = identity.GraphAccount, ["id"] = identity.GraphTenantId }));
                }

                return string.Join(Environment.NewLine, parts);
            }
        }

        public bool HasIdentity => !string.IsNullOrEmpty(IdentityText);

        /// <summary>Why nothing may be changed with these connections, or empty.</summary>
        public string IdentityProblemText
        {
            get
            {
                var identity = _identityGuard.Identity;
                var expectation = _identityGuard.Expectation;
                return string.Join(" ", _identityGuard.Problems.Select(problem => problem.Code switch
                {
                    "tenantsDiffer" => GetText(
                        UiTextKey.GetStartedIdentityTenantsDiffer,
                        "Microsoft Teams is connected to {teams}, but Microsoft Graph is signed in to another organization as {account}.",
                        new Dictionary<string, object?> { ["teams"] = identity?.TeamsTenantName, ["account"] = identity?.GraphAccount }),
                    "unexpectedTenant" => GetText(
                        UiTextKey.GetStartedIdentityUnexpectedTenant,
                        "You expected {expected}, but the sign-in reached {actual}.",
                        new Dictionary<string, object?>
                        {
                            ["expected"] = expectation.Tenant,
                            ["actual"] = identity is { TeamsKnown: true } ? identity.TeamsTenantName : identity?.GraphAccount
                        }),
                    "unexpectedAccount" => GetText(
                        UiTextKey.GetStartedIdentityUnexpectedAccount,
                        "You expected the account {expected}, but Microsoft Graph is signed in as {actual}.",
                        new Dictionary<string, object?> { ["expected"] = expectation.Account, ["actual"] = identity?.GraphAccount }),
                    _ => problem.Message
                }));
            }
        }

        public bool HasIdentityProblem => _identityGuard.Problems.Count > 0;

        partial void OnExpectedTenantChanged(string value)
        {
            _identityGuard.SetExpectation(value, _identityGuard.Expectation.Account);
            RaiseIdentityChanged();
        }

        private void RaiseIdentityChanged()
        {
            OnPropertyChanged(nameof(IdentityText));
            OnPropertyChanged(nameof(HasIdentity));
            OnPropertyChanged(nameof(IdentityProblemText));
            OnPropertyChanged(nameof(HasIdentityProblem));
            UpdateCanProceed();
        }

        /// <summary>Reads who the connections are signed in as. A read that fails changes nothing.</summary>
        private async Task RefreshIdentityAsync()
        {
            var result = await ExecutePowerShellCommandAsync(PhoneDesk.Operations.TenantIdentity.GetReadCommand(), "ReadConnectionIdentity");
            try
            {
                _identityGuard.Update(PhoneDesk.Operations.TenantIdentity.Parse(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput));
            }
            catch (System.IO.InvalidDataException)
            {
                // The sign-in itself worked; the identity stays unknown and nothing is blocked by it.
            }

            RaiseIdentityChanged();
        }

        /// <summary>
        /// Gives up the Microsoft Graph sign-in that is open, so the operator can start a new one. The
        /// old browser window cannot be closed from here; a sign-in that is finished there later
        /// connects nothing. The Microsoft Teams connection stays.
        /// </summary>
        [RelayCommand]
        private void RestartGraphSignIn()
        {
            _abandonGraphSignIn?.TrySetResult(true);
        }

        /// <summary>The profile in use when a Microsoft Graph sign-in started.</summary>
        private string? _profileAtStart;

        private void ChangeOpenSignIns(int change)
        {
            if (_sharedStateService is not null)
            {
                _sharedStateService.OpenSignIns = Math.Max(0, _sharedStateService.OpenSignIns + change);
            }
        }

        private UiTextKey? _lastSetupErrorKey;
        private string? _lastSetupErrorFallback;

        public bool CanConnectTeams => ModulesChecked && !TeamsConnected;
        public bool CanConnectGraph => ModulesChecked && !GraphConnected;

        public string SetupGuidance => CanProceed
            ? GetText(
                UiTextKey.GetStartedSetupGuidanceReady,
                "Your connections are ready. Continue to Configuration.")
            : HasIdentityProblem
                ? GetText(
                    UiTextKey.GetStartedSetupGuidanceIdentity,
                    "The sign-ins worked, but they are not the expected ones. Sign out, then sign in again with the right account.")
            : _lastSetupErrorKey is { } errorKey
                ? GetText(errorKey, _lastSetupErrorFallback ?? string.Empty)
                : !ModulesChecked
                    ? GetText(
                        UiTextKey.GetStartedSetupGuidanceStartModules,
                        "Start with Check modules.")
                    : !TeamsConnected
                        ? GetText(
                            UiTextKey.GetStartedSetupGuidanceConnectTeams,
                            "The modules are ready. Connect to Microsoft Teams next.")
                        : GetText(
                            UiTextKey.GetStartedSetupGuidanceConnectGraph,
                            "Teams is connected. Connect to Microsoft Graph next.");

        public string SetupGuidanceDetail => CanProceed
            ? GetText(
                UiTextKey.GetStartedSetupGuidanceDetailReady,
                "Your connections are ready. You can start the customer configuration now.")
            : GetText(
                UiTextKey.GetStartedSetupGuidanceDetailBlocked,
                "Complete all three checks before Guided Setup.");

        public string ModulesActionText => ModulesChecked
            ? GetText(UiTextKey.GetStartedCheckAgainAction, "Check again")
            : GetText(UiTextKey.GetStartedCheckModulesAction, "Check modules");
        public string TeamsActionText => TeamsConnected
            ? GetText(UiTextKey.GetStartedConnectedAction, "Connected")
            : ModulesChecked
                ? GetText(UiTextKey.GetStartedConnectTeamsAction, "Connect Teams")
                : GetText(UiTextKey.GetStartedCheckModulesFirstAction, "Check modules first");
        public string GraphActionText => GraphConnected
            ? GetText(UiTextKey.GetStartedConnectedAction, "Connected")
            : ModulesChecked
                ? GetText(UiTextKey.GetStartedConnectGraphAction, "Connect Graph")
                : GetText(UiTextKey.GetStartedCheckModulesFirstAction, "Check modules first");

        public string ModulesStatusText => GetText(
            UiTextKey.GetStartedStepStatus,
            "Step {step} of {total}: {title} — {status}",
            new Dictionary<string, object?>
            {
                ["step"] = 1,
                ["total"] = 3,
                ["title"] = GetText(UiTextKey.GetStartedModulesTitle, "Check PowerShell Modules"),
                ["status"] = ModulesChecked
                    ? GetText(UiTextKey.GetStartedStatusReady, "Ready")
                    : GetText(UiTextKey.GetStartedStatusNotChecked, "Not checked yet")
            });

        public string TeamsStatusText => GetText(
            UiTextKey.GetStartedStepStatus,
            "Step {step} of {total}: {title} — {status}",
            new Dictionary<string, object?>
            {
                ["step"] = 2,
                ["total"] = 3,
                ["title"] = "Microsoft Teams",
                ["status"] = TeamsConnected
                    ? GetText(UiTextKey.GetStartedStatusConnected, "Connected")
                    : GetText(UiTextKey.GetStartedStatusWaiting, "Waiting")
            });

        public string GraphStatusText => GetText(
            UiTextKey.GetStartedStepStatus,
            "Step {step} of {total}: {title} — {status}",
            new Dictionary<string, object?>
            {
                ["step"] = 3,
                ["total"] = 3,
                ["title"] = "Microsoft Graph",
                ["status"] = GraphConnected
                    ? GetText(UiTextKey.GetStartedStatusConnected, "Connected")
                    : GetText(UiTextKey.GetStartedStatusWaiting, "Waiting")
            });

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
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null,
            PhoneDesk.Operations.TenantIdentityGuard? identityGuard = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, auditLog: auditLog, translationService: translationService)
        {
            _identityGuard = identityGuard ?? new PhoneDesk.Operations.TenantIdentityGuard();
            _expectedTenant = _identityGuard.Expectation.Tenant ?? string.Empty;
            _msalAuthService = msalAuthService;
            _modulesChecked = _sessionManager.ModulesChecked;
            _teamsConnected = _sessionManager.TeamsConnected;
            _graphConnected = _sessionManager.GraphConnected;
            UpdateCanProceed();
            LogLocalized(
                UiTextKey.GetStartedPageLoadedLog,
                "Get Started page loaded",
                LogLevel.Info);

            if (_translationService is not null)
            {
                _translationService.PropertyChanged += OnTranslationServicePropertyChanged;
            }
        }

        [RelayCommand]
        private void NavigateToPage(string page)
        {
            if (!CanProceed)
            {
                LogLocalized(
                    UiTextKey.GetStartedNavigationBlockedLog,
                    "Cannot navigate: prerequisites not met",
                    LogLevel.Warning);
                return;
            }

            NavigateTo(page);
        }

        [RelayCommand]
        private async Task CheckModulesAsync()
        {
            try
            {
                ClearSetupError();
                IsBusy = true;
                LogLocalized(
                    UiTextKey.GetStartedCheckModulesLog,
                    "Checking PowerShell modules...",
                    LogLevel.Info);

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
                    LogLocalized(
                        UiTextKey.GetStartedModulesAvailableLog,
                        "PowerShell modules are available",
                        LogLevel.Success);
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
                    LogLocalized(
                        UiTextKey.GetStartedModulesMissingLog,
                        "One or more required modules could not be installed",
                        LogLevel.Error);
                    SetSetupError(
                        UiTextKey.GetStartedSetupGuidanceModulesRetry,
                        "Some required PowerShell modules are missing. Review the log, then try again.");
                }
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.GetStartedModulesCheckErrorLog,
                    "Error checking PowerShell modules: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.Message });
                ModulesChecked = false;
                _sessionManager.UpdateModulesChecked(false);
                SetSetupError(
                    UiTextKey.GetStartedSetupGuidanceModulesRetry,
                    "The module check could not be completed. Review the log and try again.");
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
                    LogLocalized(
                        UiTextKey.GetStartedConnectTeamsBlockedLog,
                        "Check the required modules before connecting to Microsoft Teams.",
                        LogLevel.Warning);
                    SetSetupError(
                        UiTextKey.GetStartedConnectTeamsBlockedLog,
                        "Check the required modules before connecting to Microsoft Teams.");
                    return;
                }

                ClearSetupError();
                IsBusy = true;
                LogLocalized(
                    UiTextKey.GetStartedConnectTeamsLog,
                    "Connecting to Microsoft Teams...",
                    LogLevel.Info);

                var command = _powerShellCommandService.GetConnectTeamsCommand();
                OperationResult<string> result;
                ChangeOpenSignIns(+1);
                try
                {
                    result = await ExecutePowerShellCommandAsync(command, "ConnectTeams");
                }
                finally
                {
                    ChangeOpenSignIns(-1);
                }

                TeamsConnected = result.HasSuccessMarker;

                string? tenantId = null;
                string? tenantName = null;

                if (TeamsConnected)
                {
                    LogLocalized(
                        UiTextKey.GetStartedConnectTeamsSuccessLog,
                        "Connected to Microsoft Teams successfully",
                        LogLevel.Success);

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
                    LogLocalized(
                        UiTextKey.GetStartedConnectTeamsErrorLog,
                        "Error connecting to Microsoft Teams: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    SetSetupError(
                        UiTextKey.GetStartedSetupGuidanceTeamsRetry,
                        "Microsoft Teams could not be connected. Check the sign-in window and try again.");
                }

                _sessionManager.UpdateTeamsConnection(TeamsConnected);
                if (tenantId != null && tenantName != null)
                {
                    _sessionManager.UpdateTenantInfo(tenantId, tenantName);
                }

                if (TeamsConnected)
                {
                    await RefreshIdentityAsync();
                }

                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.GetStartedConnectTeamsErrorLog,
                    "Error connecting to Microsoft Teams: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.Message });
                TeamsConnected = false;
                _sessionManager.UpdateTeamsConnection(false);
                UpdateCanProceed();
                SetSetupError(
                    UiTextKey.GetStartedSetupGuidanceTeamsRetry,
                    "Microsoft Teams could not be connected. Check the sign-in window and try again.");
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
                    LogLocalized(
                        UiTextKey.GetStartedConnectGraphBlockedLog,
                        "Check the required modules before connecting to Microsoft Graph.",
                        LogLevel.Warning);
                    SetSetupError(
                        UiTextKey.GetStartedConnectGraphBlockedLog,
                        "Check the required modules before connecting to Microsoft Graph.");
                    return;
                }

                ClearSetupError();
                IsBusy = true;
                _profileAtStart = _sharedStateService?.ActiveProfileLabel;
                LogLocalized(
                    UiTextKey.GetStartedConnectGraphAuthLog,
                    "Authenticating to Microsoft Graph...",
                    LogLevel.Info);

                // Step 1: Authenticate using MSAL (opens browser popup). A sign-in window that was closed
                // never reports back, so the operator can give up the wait and start again.
                var signIn = _msalAuthService.AuthenticateAsync();
                // Counted until the sign-in is used or given up, so a profile switch waits for it.
                var counted = true;
                ChangeOpenSignIns(+1);
                var abandon = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _abandonGraphSignIn = abandon;
                GraphSignInPending = true;
                try
                {
                    if (await Task.WhenAny(signIn, abandon.Task) != signIn)
                    {
                        counted = false;
                        ChangeOpenSignIns(-1);
                        // The old attempt is not awaited any more: when it finishes later, its result is not used.
                        GraphConnected = false;
                        _sessionManager.UpdateGraphConnection(false);
                        UpdateCanProceed();
                        SetSetupError(
                            UiTextKey.GetStartedGraphSignInRestarted,
                            "The Microsoft Graph sign-in was given up. Select Connect again to open a new sign-in window, and ignore the old one.");
                        return;
                    }
                }
                finally
                {
                    GraphSignInPending = false;
                    _abandonGraphSignIn = null;
                }

                var authResult = await signIn;
                if (counted)
                {
                    counted = false;
                    ChangeOpenSignIns(-1);
                }

                // Another profile was taken into use while the window was open: this sign-in belongs
                // to the customer before and is not used.
                if (_sharedStateService is not null && _profileAtStart is not null &&
                    !string.Equals(_profileAtStart, _sharedStateService.ActiveProfileLabel, StringComparison.Ordinal))
                {
                    return;
                }

                if (!authResult.Success || string.IsNullOrEmpty(authResult.AccessToken))
                {
                    LogLocalized(
                        UiTextKey.GetStartedConnectGraphAuthFailedLog,
                        "Authentication failed: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = authResult.ErrorMessage });
                    GraphConnected = false;
                    _sessionManager.UpdateGraphConnection(false);
                    UpdateCanProceed();
                    SetSetupError(
                        UiTextKey.GetStartedSetupGuidanceGraphRetry,
                        "Microsoft Graph sign-in did not complete. Finish the browser sign-in and try again.");
                    return;
                }

                LogLocalized(
                    UiTextKey.GetStartedConnectGraphPowerShellLog,
                    "Authentication successful, connecting PowerShell to Microsoft Graph...",
                    LogLevel.Info);

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
                    LogLocalized(
                        UiTextKey.GetStartedConnectGraphSuccessLog,
                        "Connected to Microsoft Graph successfully as {account}",
                        LogLevel.Success,
                        new Dictionary<string, object?> { ["account"] = account });
                }
                else
                {
                    LogLocalized(
                        UiTextKey.GetStartedConnectGraphErrorLog,
                        "Error connecting to Microsoft Graph: {details}",
                        LogLevel.Error,
                        new Dictionary<string, object?> { ["details"] = result.Value });
                    SetSetupError(
                        UiTextKey.GetStartedSetupGuidanceGraphRetry,
                        "Microsoft Graph could not be connected. Finish the browser sign-in and try again.");
                }

                _sessionManager.UpdateGraphConnection(GraphConnected, account);
                if (GraphConnected)
                {
                    await RefreshIdentityAsync();
                }

                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.GetStartedConnectGraphErrorLog,
                    "Error connecting to Microsoft Graph: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.Message });
                GraphConnected = false;
                _sessionManager.UpdateGraphConnection(false);
                UpdateCanProceed();
                SetSetupError(
                    UiTextKey.GetStartedSetupGuidanceGraphRetry,
                    "Microsoft Graph could not be connected. Finish the browser sign-in and try again.");
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
                ClearSetupError();
                IsBusy = true;
                LogLocalized(
                    UiTextKey.GetStartedDisconnectTeamsLog,
                    "Disconnecting from Microsoft Teams...",
                    LogLevel.Info);

                var command = _powerShellCommandService.GetDisconnectTeamsCommand();
                var result = await ExecutePowerShellCommandAsync(command, "DisconnectTeams");
                TeamsConnected = false;
                _sessionManager.UpdateTeamsConnection(false);
                _identityGuard.ClearIdentity();
                RaiseIdentityChanged();
                LogLocalized(
                    UiTextKey.GetStartedDisconnectTeamsSuccessLog,
                    "Disconnected from Microsoft Teams",
                    LogLevel.Info);
                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.GetStartedDisconnectTeamsErrorLog,
                    "Error disconnecting from Microsoft Teams: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.Message });
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
                ClearSetupError();
                IsBusy = true;
                LogLocalized(
                    UiTextKey.GetStartedDisconnectGraphLog,
                    "Disconnecting from Microsoft Graph...",
                    LogLevel.Info);

                // Sign out from MSAL to clear cached tokens
                await _msalAuthService.SignOutAsync();

                var command = _powerShellCommandService.GetDisconnectGraphCommand();
                var result = await ExecutePowerShellCommandAsync(command, "DisconnectGraph");
                GraphConnected = false;
                _sessionManager.UpdateGraphConnection(false);
                _identityGuard.ClearIdentity();
                RaiseIdentityChanged();
                LogLocalized(
                    UiTextKey.GetStartedDisconnectGraphSuccessLog,
                    "Disconnected from Microsoft Graph",
                    LogLevel.Info);
                UpdateCanProceed();
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.GetStartedDisconnectGraphErrorLog,
                    "Error disconnecting from Microsoft Graph: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.Message });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateCanProceed()
        {
            // Connected to another tenant than expected, or to two tenants, is not ready.
            CanProceed = ModulesChecked && TeamsConnected && GraphConnected && _identityGuard.Problems.Count == 0;
            UpdateGuidance();
        }

        private void SetSetupError(UiTextKey key, string fallback)
        {
            _lastSetupErrorKey = key;
            _lastSetupErrorFallback = fallback;
            UpdateGuidance();
        }

        private void ClearSetupError()
        {
            _lastSetupErrorKey = null;
            _lastSetupErrorFallback = null;
            UpdateGuidance();
        }

        private void UpdateGuidance()
        {
            OnPropertyChanged(nameof(SetupGuidance));
            OnPropertyChanged(nameof(SetupGuidanceDetail));
            OnPropertyChanged(nameof(ModulesActionText));
            OnPropertyChanged(nameof(TeamsActionText));
            OnPropertyChanged(nameof(GraphActionText));
            OnPropertyChanged(nameof(ModulesStatusText));
            OnPropertyChanged(nameof(TeamsStatusText));
            OnPropertyChanged(nameof(GraphStatusText));
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

        private void OnTranslationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ITranslationService.CurrentLanguage))
            {
                UpdateGuidance();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_translationService is not null)
            {
                _translationService.PropertyChanged -= OnTranslationServicePropertyChanged;
            }

            _disposed = true;
        }
    }
}
