using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class CallQueuesViewModelTests
    {
        private static CallQueuesViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new CallQueuesViewModel(
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

            harness.LoggingService.Verify(l => l.Log("Call queues page loaded", LogLevel.Info), Times.Once);
        }

        // ---- RetrieveResourceAccountsAsync ----

        [Fact]
        public async Task RetrieveResourceAccountsAsync_HappyPath_ParsesResourceAccountsAndUpdatesStatus()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("RESOURCEACCOUNT:Display Name|racq-upn@contoso.com|Identity1|Switzerland\nSUCCESS");
            var vm = CreateViewModel(harness);

            await vm.RetrieveResourceAccountsCommand.ExecuteAsync(null);

            Assert.Single(vm.ResourceAccounts);
            var account = vm.ResourceAccounts[0];
            Assert.Equal("Display Name", account.DisplayName);
            Assert.Equal("racq-upn@contoso.com", account.UserPrincipalName);
            Assert.Equal("Identity1", account.Identity);
            Assert.Equal("Switzerland", account.UsageLocation);
            Assert.Equal("Loaded 1 resource accounts starting with 'racq-'.", vm.StatusMessage);
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
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "RetrieveResourceAccounts"),
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

        // ---- RetrieveCallQueuesAsync ----

        [Fact]
        public async Task RetrieveCallQueuesAsync_HappyPath_ParsesCallQueuesAndUpdatesStatus()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("CALLQUEUE:cq-Contoso|Identity1|Longest Idle|30\nSUCCESS");
            var vm = CreateViewModel(harness);

            await vm.RetrieveCallQueuesCommand.ExecuteAsync(null);

            Assert.Single(vm.CallQueues);
            var queue = vm.CallQueues[0];
            Assert.Equal("cq-Contoso", queue.Name);
            Assert.Equal("Identity1", queue.Identity);
            Assert.Equal("Longest Idle", queue.RoutingMethod);
            Assert.Equal(30, queue.AgentAlertTime);
            Assert.Equal("Loaded 1 call queues containing 'cq-'.", vm.StatusMessage);
        }

        [Fact]
        public async Task RetrieveCallQueuesAsync_ErrorMarker_ReportsErrorAndLeavesCollectionEmpty()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: boom");
            var vm = CreateViewModel(harness);

            await vm.RetrieveCallQueuesCommand.ExecuteAsync(null);

            Assert.Empty(vm.CallQueues);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "RetrieveCallQueues"),
                Times.Once);
        }

        [Fact]
        public async Task RetrieveCallQueuesAsync_SessionExpired_ReportsConnectionErrorWithoutCallingPowerShell()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);

            await vm.RetrieveCallQueuesCommand.ExecuteAsync(null);

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
            Assert.Equal(variables.RacqUPN, vm.ResourceAccountUpn);
            Assert.Equal(variables.RacqDisplayName, vm.ResourceAccountDisplayName);
        }

        [Fact]
        public void OpenCreateResourceAccountDialogCommand_VariablesNotConfigured_UsesDefaults()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateResourceAccountDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateResourceAccountDialog);
            Assert.Equal("racq-", vm.ResourceAccountUpn);
            Assert.Equal("racq-", vm.ResourceAccountDisplayName);
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
        public void OpenCreateCallQueueDialogCommand_VariablesConfigured_PrefillsNameAndGroupId()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", M365GroupId = "group-guid" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenCreateCallQueueDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateCallQueueDialog);
            Assert.Equal(variables.CqDisplayName, vm.CallQueueName);
            Assert.Equal("group-guid", vm.M365GroupId);
        }

        [Fact]
        public void OpenCreateCallQueueDialogCommand_VariablesNotConfigured_UsesDefaults()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateCallQueueDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateCallQueueDialog);
            Assert.Equal("cq-", vm.CallQueueName);
            Assert.Equal(string.Empty, vm.M365GroupId);
        }

        [Fact]
        public void CloseCreateCallQueueDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowCreateCallQueueDialog = true;

            vm.CloseCreateCallQueueDialogCommand.Execute(null);

            Assert.False(vm.ShowCreateCallQueueDialog);
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
            Assert.Equal(variables.RacqUPN, vm.ResourceAccountUpn);
            Assert.Equal(variables.CqDisplayName, vm.CallQueueName);
        }

        [Fact]
        public void CloseAssociateDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowAssociateDialog = true;

            vm.CloseAssociateDialogCommand.Execute(null);

            Assert.False(vm.ShowAssociateDialog);
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
            Assert.Equal(variables.RacqUPN, vm.ResourceAccountUpn);
        }

        [Fact]
        public void CloseUpdateUsageLocationDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowUpdateUsageLocationDialog = true;

            vm.CloseUpdateUsageLocationDialogCommand.Execute(null);

            Assert.False(vm.ShowUpdateUsageLocationDialog);
        }

        // ---- AssignLicenseAsync ----

        [Fact]
        public async Task AssignLicenseAsync_VariablesMissing_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.Variables).Returns((PhoneManagerVariables?)null!);
            var vm = CreateViewModel(harness);

            await vm.AssignLicenseCommand.ExecuteAsync(null);

            Assert.Equal("Error: Configuration not found.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AssignLicenseAsync_MissingSkuId_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", SkuId = "" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            await vm.AssignLicenseCommand.ExecuteAsync(null);

            Assert.Equal("Error: SKU ID is not set. Set the SKU ID in Configuration first.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AssignLicenseAsync_HappyPath_ReportsSuccess()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: license assigned");
            var vm = CreateViewModel(harness);

            await vm.AssignLicenseCommand.ExecuteAsync(null);

            Assert.Contains("License assigned", vm.StatusMessage);
        }

        [Fact]
        public async Task AssignLicenseAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: license failed");
            var vm = CreateViewModel(harness);

            await vm.AssignLicenseCommand.ExecuteAsync(null);

            Assert.Contains("Error assigning the license", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "AssignLicense"),
                Times.Once);
        }

        // ---- GetM365GroupIdAsync ----

        [Fact]
        public async Task GetM365GroupIdAsync_MissingGroupName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables(); // Customer/CustomerGroupName unset -> M365Group is "ttgrp--"... but still non-whitespace.
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            // M365Group is a computed, always non-empty string ("ttgrp-{Customer}-{CustomerGroupName}"),
            // so the guard is exercised via a null Variables object instead.
            harness.SharedStateService.SetupGet(s => s.Variables).Returns((PhoneManagerVariables?)null!);

            await vm.GetM365GroupIdCommand.ExecuteAsync(null);

            Assert.Equal("Error: Configuration not found.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task GetM365GroupIdAsync_HappyPath_ParsesAndSavesGroupId()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS\nM365GROUPID:11111111-2222-3333-4444-555555555555");
            var vm = CreateViewModel(harness);

            await vm.GetM365GroupIdCommand.ExecuteAsync(null);

            Assert.Equal("11111111-2222-3333-4444-555555555555", variables.M365GroupId);
            Assert.Contains("M365 Group ID loaded and saved", vm.StatusMessage);
        }

        [Fact]
        public async Task GetM365GroupIdAsync_SuccessWithoutGroupIdLine_ReportsParseError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS");
            var vm = CreateViewModel(harness);

            await vm.GetM365GroupIdCommand.ExecuteAsync(null);

            Assert.Equal("Error: Could not parse the M365 Group ID from the result.", vm.StatusMessage);
        }

        [Fact]
        public async Task GetM365GroupIdAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: group lookup failed");
            var vm = CreateViewModel(harness);

            await vm.GetM365GroupIdCommand.ExecuteAsync(null);

            Assert.Contains("Error loading the M365 Group ID", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "GetM365GroupId"),
                Times.Once);
        }

        [Fact]
        public async Task GetM365GroupIdAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);

            await vm.GetM365GroupIdCommand.ExecuteAsync(null);

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        // ---- CreateResourceAccountAsync ----

        [Fact]
        public async Task CreateResourceAccountAsync_EmptyUpn_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = string.Empty;
            vm.ResourceAccountDisplayName = "racq-test";

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
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", CsAppCqId = "" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test";
            vm.ResourceAccountDisplayName = "racq-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Error: Call queue application ID was not found in Configuration.", vm.StatusMessage);
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
            vm.ResourceAccountUpn = "racq-test";
            vm.ResourceAccountDisplayName = "racq-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal(
                "Error: Microsoft fallback domain is not set or is invalid. Set a valid domain such as @yourdomain.com in Configuration.",
                vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateResourceAccountAsync_HappyPath_ReportsSuccessWithoutAutoRefresh()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", MsFallbackDomain = "@contoso.com" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: created");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test";
            vm.ResourceAccountDisplayName = "racq-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Contains("created successfully", vm.StatusMessage);
            Assert.False(vm.ShowCreateResourceAccountDialog);
            // CreateResourceAccountAsync deliberately does not auto-refresh, unlike AutoAttendants.
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Once);
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
            vm.ResourceAccountUpn = "racq-test";
            vm.ResourceAccountDisplayName = "racq-test";

            await vm.CreateResourceAccountCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---- UpdateUsageLocationAsync ----

        [Fact]
        public async Task UpdateUsageLocationAsync_EmptyUpn_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = string.Empty;

            await vm.UpdateUsageLocationCommand.ExecuteAsync(null);

            Assert.Equal("Error: Resource account UPN cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateUsageLocationAsync_HappyPath_ReportsSuccess()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1", UsageLocation = "CH" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: updated");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test@contoso.com";

            await vm.UpdateUsageLocationCommand.ExecuteAsync(null);

            Assert.Contains("Usage location updated", vm.StatusMessage);
            Assert.False(vm.ShowUpdateUsageLocationDialog);
        }

        [Fact]
        public async Task UpdateUsageLocationAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: update failed");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test@contoso.com";

            await vm.UpdateUsageLocationCommand.ExecuteAsync(null);

            Assert.Contains("Error updating the usage location", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Update usage location"),
                Times.Once);
        }

        // ---- CreateCallQueueAsync ----

        [Fact]
        public async Task CreateCallQueueAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CallQueueName = string.Empty;

            await vm.CreateCallQueueCommand.ExecuteAsync(null);

            Assert.Equal("Error: Call queue name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateCallQueueAsync_HappyPath_ReportsSuccessWithoutAutoRefresh()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: created");
            var vm = CreateViewModel(harness);
            vm.CallQueueName = "cq-Contoso";

            await vm.CreateCallQueueCommand.ExecuteAsync(null);

            Assert.Contains("created successfully", vm.StatusMessage);
            Assert.False(vm.ShowCreateCallQueueDialog);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateCallQueueAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: creation failed");
            var vm = CreateViewModel(harness);
            vm.CallQueueName = "cq-Contoso";

            await vm.CreateCallQueueCommand.ExecuteAsync(null);

            Assert.Contains("Error creating the call queue", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Create call queue"),
                Times.Once);
        }

        // ---- AssociateResourceAccountWithCallQueueAsync ----

        [Fact]
        public async Task AssociateResourceAccountWithCallQueueAsync_EmptyFields_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = string.Empty;
            vm.CallQueueName = "cq-Contoso";

            await vm.AssociateResourceAccountWithCallQueueCommand.ExecuteAsync(null);

            Assert.Equal("Error: Resource account UPN and call queue name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AssociateResourceAccountWithCallQueueAsync_HappyPath_ReportsSuccess()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: associated");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test@contoso.com";
            vm.CallQueueName = "cq-Contoso";

            await vm.AssociateResourceAccountWithCallQueueCommand.ExecuteAsync(null);

            Assert.Contains("was associated with call queue", vm.StatusMessage);
            Assert.False(vm.ShowAssociateDialog);
        }

        [Fact]
        public async Task AssociateResourceAccountWithCallQueueAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: association failed");
            var vm = CreateViewModel(harness);
            vm.ResourceAccountUpn = "racq-test@contoso.com";
            vm.CallQueueName = "cq-Contoso";

            await vm.AssociateResourceAccountWithCallQueueCommand.ExecuteAsync(null);

            Assert.Contains("Error associating the resource account with the call queue", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Associate resource account"),
                Times.Once);
        }

        // ---- RemoveCallQueueAsync ----

        [Fact]
        public async Task RemoveCallQueueAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CallQueueName = string.Empty;

            await vm.RemoveCallQueueCommand.ExecuteAsync(null);

            Assert.Equal("Error: Call queue name cannot be empty.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveCallQueueAsync_HappyPath_RemovesAndRefreshes()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: removed");
            var vm = CreateViewModel(harness);

            await vm.RemoveCallQueueCommand.ExecuteAsync("cq-Contoso");

            // StatusMessage reflects the auto-refresh that runs immediately after a successful remove, so
            // the final message is the retrieve's, not the remove's. The remove success is still
            // observable via the log entry.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("deleted successfully")), It.IsAny<LogLevel>()), Times.AtLeastOnce);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2));
        }

        [Fact]
        public async Task RemoveCallQueueAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: remove failed");
            var vm = CreateViewModel(harness);

            await vm.RemoveCallQueueCommand.ExecuteAsync("cq-Contoso");

            Assert.Contains("Error deleting the call queue", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(
                e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Delete call queue"),
                Times.Once);
        }

        [Fact]
        public async Task RemoveCallQueueAsync_UserDeclinesConfirmation_DoesNotExecute()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.SkipDeleteConfirmation).Returns(false);
            harness.DialogService.Setup(d => d.ShowConfirmationWithPreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);

            await vm.RemoveCallQueueCommand.ExecuteAsync("cq-Contoso");

            Assert.Equal("Operation cancelled by user", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
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

            await vm.RemoveResourceAccountCommand.ExecuteAsync("racq-test@contoso.com");

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

            await vm.RemoveResourceAccountCommand.ExecuteAsync("racq-test@contoso.com");

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        // ---- Search filtering ----

        [Fact]
        public void ResourceAccountsView_SearchText_FiltersByDisplayNameAndUpn()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ResourceAccounts.Add(new ResourceAccount("Contoso Queue", "racq-contoso@contoso.com", "Id1", "CH"));
            vm.ResourceAccounts.Add(new ResourceAccount("Fabrikam Queue", "racq-fabrikam@fabrikam.com", "Id2", "US"));

            vm.SearchResourceAccountsText = "fabrikam";

            Assert.Single(vm.ResourceAccountsView);
            Assert.Equal("Fabrikam Queue", vm.ResourceAccountsView[0].DisplayName);
        }

        [Fact]
        public void CallQueuesView_SearchText_FiltersByName()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CallQueues.Add(new CallQueue("cq-Contoso", "Id1", "Longest Idle", 30));
            vm.CallQueues.Add(new CallQueue("cq-Fabrikam", "Id2", "Attendant Routing", 15));

            vm.SearchCallQueuesText = "Contoso";

            Assert.Single(vm.CallQueuesView);
            Assert.Equal("cq-Contoso", vm.CallQueuesView[0].Name);
        }
    }
}
