using Moq;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Customer profiles: a switch keeps what the operator entered for the customer before, loads
    /// the other customer, and leaves no connection to the first one.
    /// </summary>
    public sealed class ProfilesTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "phonedesk-profiles-" + Guid.NewGuid().ToString("N"), "profiles.json");
        private readonly ViewModelTestHarness _harness = new();
        private readonly TenantProfileService _profiles;
        private readonly TenantIdentityGuard _guard = new();
        private readonly List<string> _scripts = new();
        private PhoneManagerVariables _variables = new() { Customer = "contoso", CustomerGroupName = "reception" };

        public ProfilesTests()
        {
            _profiles = new TenantProfileService(new FileTenantProfileStore(_file));
            _harness.SharedStateService.SetupGet(state => state.Variables).Returns(() => _variables);
            _harness.SharedStateService.SetupSet(state => state.Variables = It.IsAny<PhoneManagerVariables>()).Callback<PhoneManagerVariables>(value => _variables = value);
            _harness.PowerShellCommandService.Setup(c => c.GetDisconnectTeamsCommand()).Returns("DISCONNECT TEAMS");
            _harness.PowerShellCommandService.Setup(c => c.GetDisconnectGraphCommand()).Returns("DISCONNECT GRAPH");
            _harness.SessionManager.SetupGet(session => session.TeamsConnected).Returns(true);
            _harness.SessionManager.SetupGet(session => session.GraphConnected).Returns(true);
            _harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                {
                    _scripts.Add(script);
                    return new PowerShellExecutionResult { Output = "SUCCESS: done" };
                });
        }

        public void Dispose()
        {
            var directory = Path.GetDirectoryName(_file)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private ProfilesViewModel Page() => new(
            _harness.PowerShellContextService.Object, _harness.PowerShellCommandService.Object, _harness.LoggingService.Object,
            _harness.SessionManager.Object, _harness.NavigationService.Object, _harness.ErrorHandlingService.Object,
            _harness.ValidationService.Object, _harness.SharedStateService.Object, _harness.DialogService.Object,
            translationService: _harness.TranslationService, profiles: _profiles, tenantAsCode: new TenantAsCodeService(), identityGuard: _guard);

        [Fact]
        public void Service_KeepsNamesUniqueAndSavesTheConfigurationOfTheProfileThatIsLeft()
        {
            var contoso = _profiles.Create(" Contoso ", "contoso.com", "admin@contoso.com", "{\"a\":1}");
            var fabrikam = _profiles.Create("Fabrikam", null, null, "{\"b\":2}");
            Assert.Equal("Contoso", contoso.Name);
            Assert.Throws<ArgumentException>(() => _profiles.Create("contoso", null, null, string.Empty));
            Assert.Throws<ArgumentException>(() => _profiles.Create("  ", null, null, string.Empty));
            Assert.Null(_profiles.Active);

            _profiles.Activate(contoso.Id, "ignored: no profile was in use");
            Assert.Equal("{\"a\":1}", _profiles.Find("Contoso")!.ConfigurationJson);
            // The configuration on the screen belongs to the profile that is left.
            _profiles.Activate(fabrikam.Id, "{\"a\":\"edited\"}");
            Assert.Equal("{\"a\":\"edited\"}", _profiles.Find("Contoso")!.ConfigurationJson);
            Assert.Equal("Fabrikam", _profiles.Active!.Name);

            // The profiles survive a restart: a new service on the same file reads them.
            var again = new TenantProfileService(new FileTenantProfileStore(_file));
            Assert.Equal("Fabrikam", again.Active!.Name);
            Assert.Equal(2, again.List().Count);

            _profiles.Delete(fabrikam.Id);
            Assert.Null(_profiles.Active);

            // A file that cannot be read is not replaced by an empty one.
            File.WriteAllText(_file, "{ not json");
            var broken = new TenantProfileService(new FileTenantProfileStore(_file));
            Assert.Empty(broken.List());
            Assert.Throws<IOException>(() => broken.Create("New", null, null, string.Empty));
            Assert.Equal("{ not json", File.ReadAllText(_file));
        }

        [Fact]
        public async Task Switch_WaitsForASignInThatIsStillOpen()
        {
            var page = Page();
            page.ProfileName = "Contoso";
            page.CreateCommand.Execute(null);
            page.SelectedProfile = page.Profiles.Single();
            _harness.SharedStateService.SetupGet(state => state.OpenSignIns).Returns(1);

            await page.SwitchCommand.ExecuteAsync(null);

            Assert.Empty(_scripts);
            Assert.Null(_profiles.Active);
            Assert.Contains("still open", page.StatusMessage);
        }

        [Fact]
        public async Task Switch_LoadsTheOtherCustomerSetsTheExpectedTenantAndSignsOut()
        {
            var page = Page();
            page.ProfileName = "Contoso";
            page.ExpectedTenant = "contoso.com";
            page.CreateCommand.Execute(null);
            _variables = new PhoneManagerVariables { Customer = "fabrikam", CustomerGroupName = "support" };
            page.ProfileName = "Fabrikam";
            page.ExpectedTenant = "fabrikam.com";
            page.ExpectedAccount = "admin@fabrikam.com";
            page.CreateCommand.Execute(null);
            Assert.Equal(2, page.Profiles.Count);

            page.SelectedProfile = page.Profiles.Single(row => row.Name == "Contoso");
            await page.SwitchCommand.ExecuteAsync(null);

            Assert.Equal("contoso", _variables.Customer);
            Assert.Equal("contoso.com", _guard.Expectation.Tenant);
            Assert.Null(_guard.Expectation.Account);
            // The profile has its own PowerShell session: the connection of the customer before is
            // not used, and it is not signed out either, so a switch back finds it again.
            var contosoId = _profiles.Find("Contoso")!.Id;
            _harness.PowerShellContextService.Verify(host => host.UseSession(contosoId), Times.Once);
            Assert.DoesNotContain("DISCONNECT TEAMS", _scripts);
            Assert.Single(_scripts, script => script.Contains("PDIDENTITY_JSON", StringComparison.Ordinal));
            _harness.SessionManager.Verify(session => session.ResetSession(), Times.Once);
            _harness.SharedStateService.VerifySet(state => state.ActiveProfileLabel = "Contoso");
            Assert.True(page.Profiles.Single(row => row.Name == "Contoso").IsActive);
            Assert.False(page.CanSwitch);

            // The operator changes the configuration, then switches: the change is kept in the profile that is left.
            _variables.CustomerGroupName = "hotline";
            page.SelectedProfile = page.Profiles.Single(row => row.Name == "Fabrikam");
            await page.SwitchCommand.ExecuteAsync(null);
            Assert.Equal("fabrikam", _variables.Customer);
            Assert.Equal("admin@fabrikam.com", _guard.Expectation.Account);
            Assert.Contains("hotline", _profiles.Find("Contoso")!.ConfigurationJson);
        }
    }
}
