using PhoneDesk.TestShared;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The desktop half of the both-heads check: the Edit Settings page runs its real scripts in a
    /// real PowerShell engine against the simulated tenant, and the tenant must end in the state the
    /// MCP tools leave too (<see cref="SimulatedTenant.ExpectedState"/>; the MCP half is in
    /// PhoneDesk.IntegrationTests).
    /// </summary>
    public class BothHeadsEditTests
    {
        private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

        [Fact]
        public async Task DesktopPage_LeavesTheTenantInTheStateTheMcpToolsLeave()
        {
            using var tenant = new SimulatedTenantPowerShell();
            var harness = new ViewModelTestHarness();
            var page = new EditSettingsViewModel(
                tenant, harness.PowerShellCommandService.Object, harness.LoggingService.Object,
                harness.SessionManager.Object, harness.NavigationService.Object, harness.ErrorHandlingService.Object,
                harness.ValidationService.Object, harness.SharedStateService.Object, harness.DialogService.Object,
                translationService: harness.TranslationService);

            await EditAsync(page, kindIndex: 0, SimulatedTenant.QueueName, SimulatedTenant.QueueChanges);
            await EditAsync(page, kindIndex: 1, SimulatedTenant.AttendantName, SimulatedTenant.AttendantChanges);

            await SimulatedTenant.AssertExpectedStateAsync(tenant, Cancellation);
        }

        [Fact]
        public async Task DesktopPhoneNumbersPage_LeavesTheInventoryInTheStateTheMcpToolsLeave()
        {
            using var tenant = new SimulatedTenantPowerShell(SimulatedTenant.PhoneTenant);
            var harness = new ViewModelTestHarness();
            var page = new PhoneNumbersViewModel(
                tenant, harness.PowerShellCommandService.Object, harness.LoggingService.Object,
                harness.SessionManager.Object, harness.NavigationService.Object, harness.ErrorHandlingService.Object,
                harness.ValidationService.Object, harness.SharedStateService.Object, harness.DialogService.Object,
                translationService: harness.TranslationService);

            await page.LoadCommand.ExecuteAsync(null);
            page.SelectedNumber = page.Numbers.Single(row => row.Number == SimulatedTenant.FreeNumber);
            page.OpenAssignCommand.Execute(null);
            page.DialogTarget = SimulatedTenant.Ben;
            await page.CheckCommand.ExecuteAsync(null);
            Assert.True(page.CanApply, page.DialogPreview + " " + page.StatusMessage);
            await page.ApplyCommand.ExecuteAsync(null);

            await page.LoadCommand.ExecuteAsync(null);
            page.SelectedNumber = page.Numbers.Single(row => row.Number == SimulatedTenant.AnnaNumber);
            page.OpenUnassignCommand.Execute(null);
            await page.CheckCommand.ExecuteAsync(null);
            Assert.True(page.CanApply, page.DialogPreview + " " + page.StatusMessage);
            await page.ApplyCommand.ExecuteAsync(null);

            await SimulatedTenant.AssertExpectedPhoneStateAsync(tenant, Cancellation);
        }

        [Fact]
        public async Task DesktopSetupRunsPage_LeavesTheTenantInTheStateTheMcpRollbackLeaves()
        {
            var directory = Path.Combine(Path.GetTempPath(), "phonedesk-bothheads-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var tenant = new SimulatedTenantPowerShell(SimulatedTenant.RunTenant);
                var harness = new ViewModelTestHarness();
                harness.SessionManager.SetupGet(session => session.TenantId).Returns(SimulatedTenant.RunTenantId);
                harness.SessionManager.SetupGet(session => session.TenantName).Returns("Contoso");
                var sanitizer = new PhoneDesk.Services.PowerShellSanitizationService();
                var commands = new PhoneDesk.Services.PowerShellCommandService(
                    new PhoneDesk.Services.ScriptBuilders.CommonScriptBuilder(sanitizer),
                    new PhoneDesk.Services.ScriptBuilders.CallQueueScriptBuilder(sanitizer),
                    new PhoneDesk.Services.ScriptBuilders.AutoAttendantScriptBuilder(sanitizer),
                    new PhoneDesk.Services.ScriptBuilders.HolidayScriptBuilder(sanitizer),
                    new PhoneDesk.Services.ScriptBuilders.ResourceAccountScriptBuilder(sanitizer),
                    new PhoneDesk.Services.ScriptBuilders.DashboardScriptBuilder());
                var journal = new PhoneDesk.Operations.RunJournalService(new PhoneDesk.Services.FileRunJournalStore(directory), commands);
                var record = SimulatedTenant.SeedRun(journal);
                var page = new SetupRunsViewModel(
                    tenant, commands, harness.LoggingService.Object,
                    harness.SessionManager.Object, harness.NavigationService.Object, harness.ErrorHandlingService.Object,
                    harness.ValidationService.Object, harness.SharedStateService.Object, harness.DialogService.Object,
                    translationService: harness.TranslationService, journal: journal);

                Assert.Equal(record.Id, page.SelectedRun?.Id);
                await page.CheckRollbackCommand.ExecuteAsync(null);
                Assert.True(page.CanRollBack, page.Details + " " + page.StatusMessage);
                await page.RollBackCommand.ExecuteAsync(null);

                Assert.Equal(PhoneDesk.Operations.RunState.RolledBack, journal.Find(record.Id)!.State);
                await SimulatedTenant.AssertExpectedRollbackStateAsync(tenant, Cancellation);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [Fact]
        public async Task DesktopProfileSwitch_FindsTheSignInOfTheFirstCustomerAgain()
        {
            var file = Path.Combine(Path.GetTempPath(), "phonedesk-bothheads-" + Guid.NewGuid().ToString("N"), "profiles.json");
            try
            {
                using var tenant = new SimulatedTenantPowerShell(SimulatedTenant.ProfileTenant);
                var harness = new ViewModelTestHarness();
                var variables = new PhoneDesk.Models.PhoneManagerVariables { Customer = "contoso", CustomerGroupName = "reception" };
                harness.SharedStateService.SetupGet(state => state.Variables).Returns(() => variables);
                harness.SharedStateService
                    .SetupSet(state => state.Variables = Moq.It.IsAny<PhoneDesk.Models.PhoneManagerVariables>())
                    .Callback<PhoneDesk.Models.PhoneManagerVariables>(value => variables = value);
                var session = new PhoneDesk.Services.SessionManager(harness.LoggingService.Object);
                var identity = new PhoneDesk.Operations.TenantIdentityGuard();
                var profiles = new PhoneDesk.Operations.TenantProfileService(new PhoneDesk.Services.FileTenantProfileStore(file));
                var page = new ProfilesViewModel(
                    tenant, harness.PowerShellCommandService.Object, harness.LoggingService.Object,
                    session, harness.NavigationService.Object, harness.ErrorHandlingService.Object,
                    harness.ValidationService.Object, harness.SharedStateService.Object, harness.DialogService.Object,
                    translationService: harness.TranslationService, profiles: profiles,
                    tenantAsCode: new PhoneDesk.Services.TenantAsCodeService(), identityGuard: identity);
                foreach (var customer in new[] { "Contoso", "Fabrikam" })
                {
                    page.ProfileName = customer;
                    page.ExpectedTenant = customer.ToLowerInvariant() + ".com";
                    page.CreateCommand.Execute(null);
                }

                async Task SwitchAsync(string name)
                {
                    page.SelectedProfile = page.Profiles.Single(row => row.Name == name);
                    await page.SwitchCommand.ExecuteAsync(null);
                    Assert.Equal(name, profiles.Active?.Name);
                }

                await SwitchAsync("Contoso");
                SimulatedTenant.AssertNotSignedIn(session);
                await SimulatedTenant.SignInAsync(tenant, "Contoso", Cancellation);

                await SwitchAsync("Fabrikam");
                // The session of Fabrikam is new: the sign-in of Contoso is not used for it.
                SimulatedTenant.AssertNotSignedIn(session);
                await SimulatedTenant.SignInAsync(tenant, "Fabrikam", Cancellation);

                await SwitchAsync("Contoso");

                SimulatedTenant.AssertBackOnContoso(tenant, session, identity, profiles.Find("Contoso")!.Id);
            }
            finally
            {
                var directory = Path.GetDirectoryName(file)!;
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        private static async Task EditAsync(EditSettingsViewModel page, int kindIndex, string name, IReadOnlyDictionary<string, string> changes)
        {
            page.KindIndex = kindIndex;
            page.ObjectName = name;
            await page.LoadCommand.ExecuteAsync(null);
            Assert.True(page.IsLoaded, page.StatusMessage);
            foreach (var change in changes)
            {
                page.Fields.Single(field => field.Key == change.Key).NewValue = change.Value;
            }

            page.CheckCommand.Execute(null);
            Assert.True(page.CanApply, page.Summary);
            await page.ApplyCommand.ExecuteAsync(null);
            Assert.DoesNotContain("not confirmed", page.StatusMessage);
            // Nothing is left to save: the page read the tenant again and it shows every new value.
            Assert.False(page.CanApply);
            foreach (var change in changes)
            {
                var field = page.Fields.Single(row => row.Key == change.Key);
                Assert.Equal(field.NewValue, field.Current, ignoreCase: true);
            }
        }
    }
}
