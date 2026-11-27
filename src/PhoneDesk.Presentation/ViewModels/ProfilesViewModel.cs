using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Localization;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    /// <summary>A profile, as a row.</summary>
    public sealed class ProfileRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Tenant { get; init; } = string.Empty;
        public string Account { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    /// <summary>
    /// The customers of an operator as named profiles. A profile holds the expected tenant, the
    /// account and the configuration of one customer. A switch saves the configuration of the
    /// profile that was active, loads the one of the new profile, sets the expected tenant, and signs
    /// out, so the next sign-in is checked against the new customer. The rules come from
    /// <see cref="TenantProfileService"/>, the same code the MCP server uses.
    /// </summary>
    public partial class ProfilesViewModel : ViewModelBase
    {
        private readonly TenantProfileService? _profiles;
        private readonly ITenantAsCodeService? _tenantAsCode;
        private readonly TenantIdentityGuard? _identityGuard;
        private readonly IMsalGraphAuthenticationService? _graphAuthentication;

        public ObservableCollection<ProfileRow> Profiles { get; } = new();

        [ObservableProperty]
        private ProfileRow? _selectedProfile;

        [ObservableProperty]
        private string _profileName = string.Empty;

        [ObservableProperty]
        private string _expectedTenant = string.Empty;

        [ObservableProperty]
        private string _expectedAccount = string.Empty;

        public ProfilesViewModel(
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
            ITranslationService? translationService = null,
            TenantProfileService? profiles = null,
            ITenantAsCodeService? tenantAsCode = null,
            TenantIdentityGuard? identityGuard = null,
            IMsalGraphAuthenticationService? graphAuthentication = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _profiles = profiles;
            _tenantAsCode = tenantAsCode;
            _identityGuard = identityGuard;
            _graphAuthentication = graphAuthentication;
            Refresh();
        }

        public bool ShowEmptyState => Profiles.Count == 0;
        public bool HasProfiles => Profiles.Count > 0;
        public bool HasSelection => SelectedProfile is not null;
        public bool CanSwitch => SelectedProfile is { IsActive: false };

        partial void OnSelectedProfileChanged(ProfileRow? value)
        {
            if (value is not null)
            {
                ProfileName = value.Name;
                ExpectedTenant = value.Tenant;
                ExpectedAccount = value.Account;
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanSwitch));
        }

        private void Refresh(string? selectId = null)
        {
            Profiles.Clear();
            var active = _profiles?.Active?.Id;
            foreach (var profile in _profiles?.List() ?? Array.Empty<TenantProfile>())
            {
                Profiles.Add(new ProfileRow
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Tenant = profile.ExpectedTenant,
                    Account = profile.ExpectedAccount,
                    IsActive = profile.Id == active,
                    State = profile.Id == active ? GetText(UiTextKey.ProfilesStateActive, "In use") : string.Empty
                });
            }

            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasProfiles));
            SelectedProfile = Profiles.FirstOrDefault(row => row.Id == selectId) ?? Profiles.FirstOrDefault(row => row.IsActive);
        }

        private string CurrentConfiguration()
            => _tenantAsCode is null || _sharedStateService is null
                ? string.Empty
                : _tenantAsCode.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(_sharedStateService.Variables));

        /// <summary>Adds a profile with the configuration that is on the Configuration page now.</summary>
        [RelayCommand]
        private void Create()
        {
            Try(() =>
            {
                var created = _profiles!.Create(ProfileName, ExpectedTenant, ExpectedAccount, CurrentConfiguration());
                Refresh(created.Id);
                StatusMessage = GetText(
                    UiTextKey.ProfilesCreatedStatus,
                    "Profile '{name}' was created with the current configuration. Select Switch to use it.",
                    new Dictionary<string, object?> { ["name"] = created.Name });
            });
        }

        /// <summary>Saves the name, the expected tenant and the account of the selected profile.</summary>
        [RelayCommand]
        private void SaveDetails()
        {
            if (SelectedProfile is null)
            {
                return;
            }

            Try(() =>
            {
                var updated = _profiles!.Update(SelectedProfile.Id, ProfileName, ExpectedTenant, ExpectedAccount);
                if (_profiles.Active?.Id == updated.Id)
                {
                    Use(updated);
                }

                Refresh(updated.Id);
                StatusMessage = GetText(UiTextKey.ProfilesSavedStatus, "Profile '{name}' was saved.", new Dictionary<string, object?> { ["name"] = updated.Name });
            });
        }

        /// <summary>
        /// Switches to the selected profile: saves the configuration of the profile in use, loads the
        /// one of the selected profile, sets the expected tenant, and signs out of both services.
        /// </summary>
        [RelayCommand]
        private Task SwitchAsync() => RunBusyAsync(async () =>
        {
            if (SelectedProfile is null || _profiles is null || _tenantAsCode is null || _sharedStateService is null)
            {
                return;
            }

            // A sign-in window that is still open would connect the customer before after the switch.
            if (_sharedStateService.OpenSignIns > 0)
            {
                StatusMessage = GetText(
                    UiTextKey.ProfilesSignInOpenStatus,
                    "A sign-in is still open. Finish it or give it up on the Readiness Check page, then switch the profile.");
                return;
            }

            TenantProfile profile;
            try
            {
                // Read before the switch is written: a profile with a configuration that cannot be read is not used.
                var target = _profiles.Find(SelectedProfile.Id) ?? throw new ArgumentException("The profile does not exist.");
                var variables = string.IsNullOrWhiteSpace(target.ConfigurationJson)
                    ? new PhoneManagerVariables()
                    : PhoneManagerVariablesPortabilityMapper.FromDocument(_tenantAsCode.DeserializeConfiguration(target.ConfigurationJson));
                profile = _profiles.Activate(target.Id, CurrentConfiguration());
                _sharedStateService.Variables = variables;
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException or System.IO.IOException or System.Text.Json.JsonException)
            {
                StatusMessage = GetText(UiTextKey.ProfilesFailedStatus, "Not done: {details}", new Dictionary<string, object?> { ["details"] = ex.Message });
                return;
            }

            Use(profile);
            await SignOutAsync();
            Refresh(profile.Id);
            StatusMessage = GetText(
                UiTextKey.ProfilesSwitchedStatus,
                "Now using '{name}'. Its configuration is loaded and you are signed out. Sign in on the Readiness Check page; the sign-in is checked against the expected organization.",
                new Dictionary<string, object?> { ["name"] = profile.Name });
        }, nameof(SwitchAsync));

        /// <summary>Writes the configuration that is on the Configuration page now into the profile in use.</summary>
        [RelayCommand]
        private void SaveConfiguration()
        {
            Try(() =>
            {
                var active = _profiles!.Active ?? throw new ArgumentException(GetText(UiTextKey.ProfilesNoneActive, "No profile is in use."));
                _profiles.SaveConfiguration(active.Id, CurrentConfiguration());
                StatusMessage = GetText(UiTextKey.ProfilesConfigurationSavedStatus, "The current configuration was saved in '{name}'.", new Dictionary<string, object?> { ["name"] = active.Name });
            });
        }

        [RelayCommand]
        private async Task DeleteAsync()
        {
            if (SelectedProfile is null || _profiles is null)
            {
                return;
            }

            var name = SelectedProfile.Name;
            if (_dialogService is not null && !await _dialogService.ShowConfirmationAsync(
                    GetText(UiTextKey.ProfilesDeleteTitle, "Delete the profile?"),
                    GetText(
                        UiTextKey.ProfilesDeleteMessage,
                        "The profile '{name}' and its saved configuration are removed from this computer. The tenant of the customer is not touched.",
                        new Dictionary<string, object?> { ["name"] = name })))
            {
                return;
            }

            Try(() =>
            {
                var wasActive = SelectedProfile.IsActive;
                _profiles.Delete(SelectedProfile.Id);
                if (wasActive && _sharedStateService is not null)
                {
                    _sharedStateService.ActiveProfileLabel = string.Empty;
                }

                Refresh();
                StatusMessage = GetText(UiTextKey.ProfilesDeletedStatus, "Profile '{name}' was deleted.", new Dictionary<string, object?> { ["name"] = name });
            });
        }

        /// <summary>Makes a profile what the application works with: the shell shows it, and a sign-in is checked against it.</summary>
        private void Use(TenantProfile profile)
        {
            _identityGuard?.SetExpectation(profile.ExpectedTenant, profile.ExpectedAccount);
            if (_sharedStateService is not null)
            {
                _sharedStateService.ActiveProfileLabel = profile.Name;
            }
        }

        /// <summary>
        /// Signs out of both services, so the connection of the customer before is not used for the
        /// new one. What was read from that tenant and the progress of a setup are forgotten too.
        /// </summary>
        private async Task SignOutAsync()
        {
            if (_sessionManager.TeamsConnected)
            {
                await ExecutePowerShellCommandAsync(_powerShellCommandService.GetDisconnectTeamsCommand(), "DisconnectTeams");
            }

            if (_sessionManager.GraphConnected)
            {
                if (_graphAuthentication is not null)
                {
                    await _graphAuthentication.SignOutAsync();
                }

                await ExecutePowerShellCommandAsync(_powerShellCommandService.GetDisconnectGraphCommand(), "DisconnectGraph");
            }

            var modulesChecked = _sessionManager.ModulesChecked;
            _sessionManager.ResetSession();
            _sessionManager.UpdateModulesChecked(modulesChecked);
            _identityGuard?.ClearIdentity();
            if (_sharedStateService is not null)
            {
                _sharedStateService.WizardProgress = null;
                _sharedStateService.SetupRun = null;
            }
        }

        private void Try(Action action)
        {
            if (_profiles is null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                StatusMessage = GetText(UiTextKey.ProfilesFailedStatus, "Not done: {details}", new Dictionary<string, object?> { ["details"] = ex.Message });
            }
        }
    }
}
