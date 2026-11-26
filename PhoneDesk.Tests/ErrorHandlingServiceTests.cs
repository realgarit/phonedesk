using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests
{
    public class ErrorHandlingServiceTests
    {
        private static (ErrorHandlingService service, Mock<ILoggingService> logger, Mock<IDialogService> dialog) CreateService()
        {
            var logger = new Mock<ILoggingService>();
            var dialog = new Mock<IDialogService>();
            dialog.Setup(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            var service = new ErrorHandlingService(logger.Object, dialog.Object);
            return (service, logger, dialog);
        }

        [Fact]
        public async Task HandlePowerShellError_LogsErrorAndShowsDialogWithCorrectTitle()
        {
            var (service, logger, dialog) = CreateService();

            await service.HandlePowerShellError("Get-Thing", "boom", "MyContext");

            logger.Verify(l => l.Log(
                It.Is<string>(m => m.Contains("PowerShell Error in MyContext") && m.Contains("Get-Thing") && m.Contains("boom")),
                LogLevel.Error), Times.Once);

            dialog.Verify(d => d.ShowMessageAsync(
                ConstantsService.ErrorDialogTitles.PowerShellError,
                It.Is<string>(m => m.Contains("boom"))), Times.Once);
        }

        [Fact]
        public async Task HandlePowerShellError_StripsCarriageReturnsAndNewlinesFromCommand()
        {
            var (service, logger, _) = CreateService();

            await service.HandlePowerShellError("Get-Thing\r\nContinued", "boom", "MyContext");

            logger.Verify(l => l.Log(
                It.Is<string>(m => !m.Contains("\r") && m.Contains("Get-Thing Continued")),
                LogLevel.Error), Times.Once);
        }

        [Fact]
        public async Task HandleValidationError_LogsWarningAndShowsDialogWithCorrectTitle()
        {
            var (service, logger, dialog) = CreateService();

            await service.HandleValidationError("Field is required", "MyContext");

            logger.Verify(l => l.Log(
                It.Is<string>(m => m.Contains("Validation Error in MyContext") && m.Contains("Field is required")),
                LogLevel.Warning), Times.Once);

            dialog.Verify(d => d.ShowMessageAsync(
                ConstantsService.ErrorDialogTitles.ValidationError,
                "Field is required"), Times.Once);
        }

        [Fact]
        public async Task HandleConnectionError_LogsErrorAndShowsDialogWithCorrectTitle()
        {
            var (service, logger, dialog) = CreateService();

            await service.HandleConnectionError("Graph", "timeout");

            logger.Verify(l => l.Log(
                It.Is<string>(m => m.Contains("Failed to connect to Graph") && m.Contains("timeout")),
                LogLevel.Error), Times.Once);

            dialog.Verify(d => d.ShowMessageAsync(
                ConstantsService.ErrorDialogTitles.ConnectionError,
                It.Is<string>(m => m.Contains("Graph") && m.Contains("timeout"))), Times.Once);
        }

        [Fact]
        public async Task HandleConfirmation_LogsInfoAndReturnsTrueWhenUserConfirms()
        {
            var (service, logger, dialog) = CreateService();
            dialog.Setup(d => d.ShowConfirmationAsync(ConstantsService.ErrorDialogTitles.Confirmation, "Are you sure?"))
                .ReturnsAsync(true);

            var result = await service.HandleConfirmation("Are you sure?");

            Assert.True(result);
            logger.Verify(l => l.Log(
                It.Is<string>(m => m.Contains("User confirmation requested") && m.Contains("Are you sure?")),
                LogLevel.Info), Times.Once);
            dialog.Verify(d => d.ShowConfirmationAsync(ConstantsService.ErrorDialogTitles.Confirmation, "Are you sure?"), Times.Once);
        }
    }
}
