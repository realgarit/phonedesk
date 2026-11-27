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
