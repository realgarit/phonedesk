using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class HolidaysViewModelTests
    {
        private static HolidaysViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new HolidaysViewModel(
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

            harness.LoggingService.Verify(l => l.Log("Holidays page loaded", LogLevel.Info), Times.Once);
        }

        [Fact]
        public void ResetHolidayStateCommand_ResetsFieldsAndStatus()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.IsHolidayCreated = true;
            vm.HolidayName = "hd-old";

            vm.ResetHolidayStateCommand.Execute(null);

            Assert.False(vm.IsHolidayCreated);
            Assert.Equal(string.Empty, vm.HolidayName);
            Assert.Equal("Holiday state reset. You can now create a new holiday.", vm.StatusMessage);
        }

        [Fact]
        public void OpenCreateHolidayDialogCommand_VariablesConfigured_PrefillsFromVariables()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            var vm = CreateViewModel(harness);

            vm.OpenCreateHolidayDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateHolidayDialog);
            Assert.Equal(variables.HolidayName, vm.HolidayName);
        }

        [Fact]
        public void OpenCreateHolidayDialogCommand_VariablesNotConfigured_UsesDefaults()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCreateHolidayDialogCommand.Execute(null);

            Assert.True(vm.ShowCreateHolidayDialog);
            Assert.Equal("hd-", vm.HolidayName);
        }

        [Fact]
        public void CloseCreateHolidayDialogCommand_HidesDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowCreateHolidayDialog = true;

            vm.CloseCreateHolidayDialogCommand.Execute(null);

            Assert.False(vm.ShowCreateHolidayDialog);
        }

        [Fact]
        public async Task CreateHolidayAsync_NoHolidaysConfigured_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.CreateHolidayCommand.ExecuteAsync(null);

            Assert.Equal("Error: No holidays configured. Please add holidays in the Variables page first.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateHolidayAsync_HappyPath_SetsIsHolidayCreatedTrue()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            variables.HolidaySeries.Add(new HolidayEntry(DateTime.Today, TimeSpan.Zero));
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: holiday series created");
            var vm = CreateViewModel(harness);

            await vm.CreateHolidayCommand.ExecuteAsync(null);

            Assert.True(vm.IsHolidayCreated);
            Assert.Contains("created successfully", vm.StatusMessage);
        }

        [Fact]
        public async Task CreateHolidayAsync_ErrorPath_SetsIsHolidayCreatedFalse()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            variables.HolidaySeries.Add(new HolidayEntry(DateTime.Today, TimeSpan.Zero));
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: boom");
            var vm = CreateViewModel(harness);

            await vm.CreateHolidayCommand.ExecuteAsync(null);

            Assert.False(vm.IsHolidayCreated);
            Assert.Contains("Error creating holiday series", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Create Holiday Schedule"), Times.Once);
        }

        [Fact]
        public async Task CreateHolidayAsync_UserCancelsPreview_ReturnsWithoutExecutingAndDoesNotSetCreated()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            variables.HolidaySeries.Add(new HolidayEntry(DateTime.Today, TimeSpan.Zero));
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SharedStateService.SetupGet(s => s.SkipScriptPreview).Returns(false);
            harness.DialogService.Setup(d => d.ShowScriptPreviewAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            var vm = CreateViewModel(harness);

            await vm.CreateHolidayCommand.ExecuteAsync(null);

            Assert.Equal("Operation cancelled by user", vm.StatusMessage);
            Assert.False(vm.IsHolidayCreated);
        }

        [Fact]
        public async Task VerifyAutoAttendantAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = string.Empty;

            await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);

            Assert.Equal("Error: Auto attendant name cannot be empty", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task VerifyAutoAttendantAsync_HappyPath_ReportsSuccess()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("SUCCESS: verified");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("verified successfully", vm.StatusMessage);
        }

        [Fact]
        public async Task VerifyAutoAttendantAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: not found");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("not found or not accessible", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "VerifyAutoAttendant"), Times.Once);
        }

        [Fact]
        public async Task VerifyAutoAttendantAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";

            await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);

            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task AttachHolidayToAutoAttendantAsync_EmptyName_ReportsErrorWithoutExecuting()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = string.Empty;

            await vm.AttachHolidayToAutoAttendantCommand.ExecuteAsync(null);

            Assert.Equal("Error: Auto attendant name cannot be empty", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AttachHolidayToAutoAttendantAsync_HappyPath_ReportsSuccess()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("SUCCESS: attached");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";
            vm.HolidayName = "hd-Contoso";

            await vm.AttachHolidayToAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("Successfully attached", vm.StatusMessage);
        }

        [Fact]
        public async Task AttachHolidayToAutoAttendantAsync_ErrorPath_ReportsError()
        {
            var harness = new ViewModelTestHarness();
            var variables = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Group1" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);
            harness.SetExecutionResult("ERROR: attach failed");
            var vm = CreateViewModel(harness);
            vm.AutoAttendantName = "aa-Contoso";
            vm.HolidayName = "hd-Contoso";

            await vm.AttachHolidayToAutoAttendantCommand.ExecuteAsync(null);

            Assert.Contains("Error attaching holiday to auto attendant", vm.StatusMessage);
            harness.ErrorHandlingService.Verify(e => e.HandlePowerShellError(It.IsAny<string>(), It.IsAny<string>(), "Link Holiday to Auto Attendant"), Times.Once);
        }
    }
}
