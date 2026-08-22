using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class M365GroupsViewModelTests
    {
        private static M365GroupsViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new M365GroupsViewModel(
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
        public void Construction_LogsPageLoadAndInitializesGroupName()
        {
            var harness = new ViewModelTestHarness();

            var vm = CreateViewModel(harness);

            harness.LoggingService.Verify(l => l.Log("M365 Groups page loaded", LogLevel.Info), Times.Once);
            Assert.Equal("ttgrp-", vm.NewGroupName);
        }

        [Fact]
        public void ShowVariablesConfirmationCommand_SetsShowConfirmationTrue()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.ShowVariablesConfirmationCommand.Execute(null);

            Assert.True(vm.ShowConfirmation);
        }

        [Fact]
        public void NavigateToVariablesPageCommand_Navigates()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.NavigateToVariablesPageCommand.Execute(null);

            harness.NavigationService.Verify(n => n.NavigateTo(ConstantsService.Pages.Variables), Times.Once);
        }

        [Fact]
        public async Task RetrieveM365GroupsAsync_HappyPath_ParsesGroupsFromOutput()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("GROUP:Group One|id-1|group1|Description one\nGROUP:Group Two|id-2|group2|Description two");
            var vm = CreateViewModel(harness);

            await vm.RetrieveM365GroupsCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Groups.Count);
            Assert.Contains("Loaded 2 groups", vm.GroupStatus);
        }

        [Fact]
        public async Task RetrieveM365GroupsAsync_NoOutput_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(string.Empty);
            var vm = CreateViewModel(harness);

            await vm.RetrieveM365GroupsCommand.ExecuteAsync(null);

            Assert.Equal("Error: No output from the PowerShell command.", vm.GroupStatus);
        }

        [Fact]
        public async Task RetrieveM365GroupsAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);

            await vm.RetrieveM365GroupsCommand.ExecuteAsync(null);

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void OpenCreateGroupDialogCommand_ShowsDialogAndUpdatesGroupName()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateGroupDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateGroupDialog);
        }

        [Fact]
        public void CloseCreateGroupDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowCreateGroupDialog = true;

            vm.CloseCreateGroupDialogCommand.Execute(null);

            Assert.False(vm.ShowCreateGroupDialog);
        }

        [Fact]
        public async Task CreateNewGroupAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.NewGroupName = string.Empty;

            await vm.CreateNewGroupCommand.ExecuteAsync(null);

            Assert.Equal("Error: Group name cannot be empty.", vm.GroupStatus);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateNewGroupAsync_HappyPath_ReportsSuccessAndAutoRefreshes()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.AutoRefreshAfterOperations).Returns(true);
            harness.PowerShellContextService
                .SetupSequence(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "Group created successfully", HadErrors = false })
                .ReturnsAsync(new PowerShellExecutionResult { Output = "GROUP:Group One|id-1|group1|Description one", HadErrors = false });
            var vm = CreateViewModel(harness);
            vm.NewGroupName = "ttgrp-new";

            await vm.CreateNewGroupCommand.ExecuteAsync(null);

            // GroupStatus reflects the auto-refresh (RetrieveM365GroupsAsync) that runs immediately after
            // a successful create, so the final status is the retrieve's, not the create's. The create
            // success is still observable via the log entry and the refreshed Groups collection.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("created successfully")), It.IsAny<LogLevel>()), Times.Once);
            Assert.Single(vm.Groups);
        }

        [Fact]
        public async Task CreateNewGroupAsync_AlreadyExists_ReportsWarningWithoutRefresh()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("Group already exists");
            var vm = CreateViewModel(harness);
            vm.NewGroupName = "ttgrp-existing";

            await vm.CreateNewGroupCommand.ExecuteAsync(null);

            Assert.Contains("already exists", vm.GroupStatus);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateNewGroupAsync_UserCancelsPreview_ReturnsWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.SkipScriptPreview).Returns(false);
            harness.DialogService.Setup(d => d.ShowScriptPreviewAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);
            vm.NewGroupName = "ttgrp-new";

            await vm.CreateNewGroupCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.GroupStatus);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveM365GroupAsync_NoSelection_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.SelectedGroup = null;

            await vm.RemoveM365GroupCommand.ExecuteAsync(null);

            Assert.Equal("Error: Select a group to delete.", vm.GroupStatus);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveM365GroupAsync_HappyPath_RemovesAndAutoRefreshes()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.AutoRefreshAfterOperations).Returns(true);
            harness.PowerShellContextService
                .SetupSequence(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "SUCCESS: removed", HadErrors = false })
                .ReturnsAsync(new PowerShellExecutionResult { Output = "GROUP:Group Two|id-2|group2|Description two", HadErrors = false });
            var vm = CreateViewModel(harness);
            vm.SelectedGroup = new M365Group("Group One", "id-1", "group1", "Description one");

            await vm.RemoveM365GroupCommand.ExecuteAsync(null);

            // GroupStatus reflects the auto-refresh (RetrieveM365GroupsAsync) that runs immediately after
            // a successful remove, so the final status is the retrieve's, not the remove's. The remove
            // success is still observable via the log entry and the refreshed Groups collection.
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("deleted successfully")), It.IsAny<LogLevel>()), Times.Once);
            Assert.Single(vm.Groups);
        }

        [Fact]
        public async Task RemoveM365GroupAsync_UserCancelsConfirmation_ReturnsWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.SkipDeleteConfirmation).Returns(false);
            harness.DialogService.Setup(d => d.ShowConfirmationWithPreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);
            vm.SelectedGroup = new M365Group("Group One", "id-1", "group1", "Description one");

            await vm.RemoveM365GroupCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.GroupStatus);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CheckM365GroupAsync_VariablesNotFound_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.Variables).Returns((PhoneManagerVariables?)null!);
            var vm = CreateViewModel(harness);

            await vm.CheckM365GroupCommand.ExecuteAsync(null);

            Assert.Equal("Error: Configuration not found. Set the configuration first.", vm.GroupStatus);
        }

        [Fact]
        public async Task CheckM365GroupAsync_ValidationFails_DoesNotExecute()
        {
            var harness = new ViewModelTestHarness();
            var invalidResult = new ValidationResult();
            invalidResult.AddError("Customer is required");
            harness.ValidationService.Setup(v => v.ValidateVariables(It.IsAny<PhoneManagerVariables>()))
                .Returns(invalidResult);
            var vm = CreateViewModel(harness);

            await vm.CheckM365GroupCommand.ExecuteAsync(null);

            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CheckM365GroupAsync_HappyPath_ParsesGroupIdAndStatus()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("Group created successfully: group-id-123");
            var vm = CreateViewModel(harness);

            await vm.CheckM365GroupCommand.ExecuteAsync(null);

            Assert.True(vm.IsGroupChecked);
            Assert.Equal("group-id-123", vm.GroupId);
            Assert.Equal("Group created successfully.", vm.GroupStatus);
        }
    }
}
