using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class AutoAttendantsViewModelTests
    {
        private static AutoAttendantsViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new AutoAttendantsViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object);

        [Fact]
        public void Construction_LogsPageLoad()
        {
            var harness = new ViewModelTestHarness();

            CreateViewModel(harness);

            harness.LoggingService.Verify(l => l.Log("Auto attendants page loaded", LogLevel.Info), Times.Once);
        }

        // ---- RetrieveResourceAccountsAsync ----

        [Fact]
        public async Task RetrieveResourceAccountsAsync_HappyPath_ParsesResourceAccountsAndUpdatesStatus()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("RESOURCEACCOUNT:Display Name|raaa-upn@contoso.com|Identity1|Switzerland\nSUCCESS");
            var vm = CreateViewModel(harness);

            await vm.RetrieveResourceAccountsCommand.ExecuteAsync(null);

            Assert.Single(vm.ResourceAccounts);
            var account = vm.ResourceAccounts[0];
            Assert.Equal("Display Name", account.DisplayName);
            Assert.Equal("raaa-upn@contoso.com", account.UserPrincipalName);
            Assert.Equal("Identity1", account.Identity);
            Assert.Equal("Switzerland", account.UsageLocation);
            Assert.Equal("Loaded 1 resource accounts starting with 'raaa-'.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
        }

        [Fact]
        public async Task RetrieveResourceAccountsAsync_ErrorMarker_ReportsErrorAndLeavesCollectionEmpty()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: boom");
            var vm = CreateViewModel(harness);

            await vm.RetrieveResourceAccountsCommand.ExecuteAsync(null);

            Assert.Empty(vm.ResourceAccounts);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "RetrieveAutoAttendantResourceAccounts"),
                Times.Once);
        }

        [Fact]
        public async Task RetrieveResourceAccountsAsync_EmptyOutput_SetsNoOutputError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(string.Empty);
            var vm = CreateViewModel(harness);

            await vm.RetrieveResourceAccountsCommand.ExecuteAsync(null);

            Assert.Empty(vm.ResourceAccounts);
            Assert.Equal("Error: No output from the PowerShell command.", vm.StatusMessage);
        }

        [Fact]
        public async Task RetrieveResourceAccountsAsync_SessionExpired_ReportsConnectionErrorWithoutCallingPowerShell()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);

            await vm.RetrieveResourceAccountsCommand.ExecuteAsync(null);

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---- RetrieveAutoAttendantsAsync ----

        [Fact]
        public async Task RetrieveAutoAttendantsAsync_HappyPath_ParsesAutoAttendantsAndUpdatesStatus()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("AUTOATTENDANT:aa-Contoso|Identity1|en-US|W. Europe Standard Time\nSUCCESS");
            var vm = CreateViewModel(harness);

            await vm.RetrieveAutoAttendantsCommand.ExecuteAsync(null);

            Assert.Single(vm.AutoAttendants);
            var aa = vm.AutoAttendants[0];
            Assert.Equal("aa-Contoso", aa.Name);
            Assert.Equal("Identity1", aa.Identity);
            Assert.Equal("en-US", aa.LanguageId);
            Assert.Equal("W. Europe Standard Time", aa.TimeZoneId);
            Assert.Equal("Loaded 1 auto attendants containing 'aa-'.", vm.StatusMessage);
        }

        [Fact]
        public async Task RetrieveAutoAttendantsAsync_ErrorMarker_ReportsErrorAndLeavesCollectionEmpty()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: boom");
            var vm = CreateViewModel(harness);

            await vm.RetrieveAutoAttendantsCommand.ExecuteAsync(null);

            Assert.Empty(vm.AutoAttendants);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "RetrieveAutoAttendants"),
                Times.Once);
        }

        [Fact]
        public async Task RetrieveAutoAttendantsAsync_SessionExpired_ReportsConnectionErrorWithoutCallingPowerShell()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);

            await vm.RetrieveAutoAttendantsCommand.ExecuteAsync(null);

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---- Open/close dialog commands ----

        [Fact]
        public void OpenCreateResourceAccountDialogCommand_VariablesConfigured_PrefillsFromVariables()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenCreateResourceAccountDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateResourceAccountDialog);
            Assert.Equal(variables.RaaaUPN, vm.ResourceAccountUpn);
            Assert.Equal(variables.RaaaDisplayName, vm.ResourceAccountDisplayName);
        }

        [Fact]
        public void OpenCreateResourceAccountDialogCommand_VariablesNotConfigured_UsesDefaults()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateResourceAccountDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateResourceAccountDialog);
            Assert.Equal("raaa-", vm.ResourceAccountUpn);
            Assert.Equal("raaa-", vm.ResourceAccountDisplayName);
        }

        [Fact]
        public void CloseCreateResourceAccountDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowCreateResourceAccountDialog = true;

            vm.CloseCreateResourceAccountDialogCommand.Execute(null);

            Assert.False(vm.ShowCreateResourceAccountDialog);
        }

        [Fact]
        public void OpenValidateCallQueueDialogCommand_VariablesConfigured_PrefillsCallQueueUpn()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenValidateCallQueueDialogCommand.Execute(null);

            Assert.True(vm.ShowValidateCallQueueDialog);
            Assert.Equal(variables.RacqUPN, vm.CallQueueUpn);
        }

        [Fact]
        public void OpenValidateCallQueueDialogCommand_VariablesNotConfigured_UsesDefault()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenValidateCallQueueDialogCommand.Execute(null);

            Assert.True(vm.ShowValidateCallQueueDialog);
            Assert.Equal("racq-", vm.CallQueueUpn);
        }

        [Fact]
        public void CloseValidateCallQueueDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowValidateCallQueueDialog = true;

            vm.CloseValidateCallQueueDialogCommand.Execute(null);

            Assert.False(vm.ShowValidateCallQueueDialog);
        }

        [Fact]
        public void OpenCreateAutoAttendantDialogCommand_VariablesConfigured_PrefillsName()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenCreateAutoAttendantDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateAutoAttendantDialog);
            Assert.Equal(variables.AaDisplayName, vm.AutoAttendantName);
        }

        [Fact]
        public void OpenCreateAutoAttendantDialogCommand_VariablesNotConfigured_UsesDefault()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateAutoAttendantDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateAutoAttendantDialog);
            Assert.Equal("aa-", vm.AutoAttendantName);
        }

        [Fact]
        public void OpenAssociateDialogCommand_VariablesConfigured_PrefillsUpnAndName()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenAssociateDialogCommand.Execute(null);

            Assert.True(vm.ShowAssociateDialog);
            Assert.Equal(variables.RaaaUPN, vm.ResourceAccountUpn);
            Assert.Equal(variables.AaDisplayName, vm.AutoAttendantName);
        }

        [Fact]
        public void OpenUpdateUsageLocationDialogCommand_VariablesConfigured_PrefillsUpn()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenUpdateUsageLocationDialogCommand.Execute(null);

            Assert.True(vm.ShowUpdateUsageLocationDialog);
            Assert.Equal(variables.RaaaUPN, vm.ResourceAccountUpn);
        }

        // ---- CreateResourceAccountAsync ----

        [Fact]
        public async Task CreateResourceAccountAsync_EmptyUpn_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = string.Empty;
            vm.ResourceAccountDisplayName = "raaa-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Error: Resource account UPN and display name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateResourceAccountAsync_MissingAppId_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", CsAppAaId = "" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "raaa-test";
            vm.ResourceAccountDisplayName = "raaa-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Error: Auto attendant application ID was not found in Configuration.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateResourceAccountAsync_InvalidFallbackDomain_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            // Default harness variables have an empty MsFallbackDomain, which fails validation.
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "raaa-test";
            vm.ResourceAccountDisplayName = "raaa-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal(
                "Error: Microsoft fallback domain is not set or is invalid. Set a valid domain such as @yourdomain.com in Configuration.",
                vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateResourceAccountAsync_HappyPath_CreatesAndRefreshesResourceAccounts()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", MsFallbackDomain = "@contoso.com" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: created");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "raaa-test";
            vm.ResourceAccountDisplayName = "raaa-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            // StatusMessage reflects the auto-refresh that runs immediately after a successful create, so
            // the final message is the retrieve's, not the create's. The create success is still
            // observable via the log entry.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("created successfully")), It.IsAny<LogLevel>()), Times.AtLeastOnce);
            Assert.False(vm.ShowCreateResourceAccountDialog);
            // AutoRefreshAfterOperations is true by default, so a retrieve should have run afterwards.
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2));
        }

        [Fact]
        public async Task CreateResourceAccountAsync_UserCancelsPreview_SetsCancelledStatusWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", MsFallbackDomain = "@contoso.com" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SharedStateService.SetupGet(s => s.SkipScriptPreview).Returns(false);
            harness.DialogService.Setup(d => d.ShowScriptPreviewAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "raaa-test";
            vm.ResourceAccountDisplayName = "raaa-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---- CreateAutoAttendantAsync ----

        [Fact]
        public async Task CreateAutoAttendantAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = string.Empty;

            await vm.CreateAutoAttendantCommand.ExecuteAsync(null);

            Assert.Equal("Error: Auto attendant name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateAutoAttendantAsync_HappyPath_CreatesAndRefreshesAutoAttendants()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: created");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.CreateAutoAttendantCommand.ExecuteAsync(null);

            // StatusMessage reflects the auto-refresh that runs immediately after a successful create, so
            // the final message is the retrieve's, not the create's. The create success is still
            // observable via the log entry.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("created successfully")), It.IsAny<LogLevel>()), Times.AtLeastOnce);
            Assert.False(vm.ShowCreateAutoAttendantDialog);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2));
        }

        [Fact]
        public async Task CreateAutoAttendantAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: creation failed");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.CreateAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("Error creating the auto attendant", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Create auto attendant"),
                Times.Once);
        }

        // ---- RemoveAutoAttendantAsync ----

        [Fact]
        public async Task RemoveAutoAttendantAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = string.Empty;

            await vm.RemoveAutoAttendantCommand.ExecuteAsync(null);

            Assert.Equal("Error: Auto attendant name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveAutoAttendantAsync_HappyPath_RemovesAndRefreshes()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: removed");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.RemoveAutoAttendantCommand.ExecuteAsync(null);

            // StatusMessage reflects the auto-refresh that runs immediately after a successful remove, so
            // the final message is the retrieve's, not the remove's. The remove success is still
            // observable via the log entry.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("deleted successfully")), It.IsAny<LogLevel>()), Times.AtLeastOnce);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2));
        }

        [Fact]
        public async Task RemoveAutoAttendantAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: remove failed");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.RemoveAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("Error deleting the auto attendant", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Delete auto attendant"),
                Times.Once);
        }

        [Fact]
        public async Task RemoveAutoAttendantAsync_UserDeclinesConfirmation_DoesNotExecute()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.SkipDeleteConfirmation).Returns(false);
            harness.DialogService.Setup(d => d.ShowConfirmationWithPreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.RemoveAutoAttendantCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---- RemoveScheduleAsync ----

        [Fact]
        public async Task RemoveScheduleAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.RemoveScheduleCommand.ExecuteAsync(string.Empty);

            Assert.Equal("Error: Schedule name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveScheduleAsync_HappyPath_RemovesSuccessfully()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: removed");
            var vm = CreateViewModel(harness);

            await vm.RemoveScheduleCommand.ExecuteAsync("hd-Contoso");

            Assert.Contains("deleted successfully", vm.StatusMessage);
        }

        [Fact]
        public async Task RemoveScheduleAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: remove failed");
            var vm = CreateViewModel(harness);

            await vm.RemoveScheduleCommand.ExecuteAsync("hd-Contoso");

            Assert.Contains("Error deleting the schedule", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Delete schedule"),
                Times.Once);
        }

        // ---- RemoveResourceAccountAsync ----

        [Fact]
        public async Task RemoveResourceAccountAsync_EmptyUpn_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = string.Empty;

            await vm.RemoveResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Error: Resource account UPN cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveResourceAccountAsync_HappyPath_RemovesAndRefreshes()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: removed");
            var vm = CreateViewModel(harness);

            await vm.RemoveResourceAccountCommand.ExecuteAsync("raaa-test@contoso.com");

            // StatusMessage reflects the auto-refresh that runs immediately after a successful remove, so
            // the final message is the retrieve's, not the remove's. The remove success is still
            // observable via the log entry.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("deleted successfully")), It.IsAny<LogLevel>()), Times.AtLeastOnce);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2));
        }

        [Fact]
        public async Task RemoveResourceAccountAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            harness.SharedStateService.SetupGet(s => s.SkipDeleteConfirmation).Returns(true);
            var vm = CreateViewModel(harness);

            await vm.RemoveResourceAccountCommand.ExecuteAsync("raaa-test@contoso.com");

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        // ---- Search filtering ----

        [Fact]
        public void ResourceAccountsView_SearchText_FiltersByDisplayNameAndUpn()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccounts.Add(new ResourceAccount("Contoso Reception", "raaa-contoso@contoso.com", "Id1", "CH"));
            vm.ResourceAccounts.Add(new ResourceAccount("Fabrikam Reception", "raaa-fabrikam@fabrikam.com", "Id2", "US"));

            vm.SearchResourceAccountsText = "contoso";

            Assert.Single(vm.ResourceAccountsView);
            Assert.Equal("Contoso Reception", vm.ResourceAccountsView[0].DisplayName);
        }

        [Fact]
        public void AutoAttendantsView_SearchText_FiltersByName()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.AutoAttendants.Add(new AutoAttendant("aa-Contoso", "Id1", "en-US", "W. Europe Standard Time"));
            vm.AutoAttendants.Add(new AutoAttendant("aa-Fabrikam", "Id2", "en-US", "W. Europe Standard Time"));

            vm.SearchAutoAttendantsText = "Fabrikam";

            Assert.Single(vm.AutoAttendantsView);
            Assert.Equal("aa-Fabrikam", vm.AutoAttendantsView[0].Name);
        }
    }
}
