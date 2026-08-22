using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class DocumentationViewModelTests
    {
        private static Mock<IDocumentationScriptBuilder> CreateDocBuilderMock()
        {
            var mock = new Mock<IDocumentationScriptBuilder>();
            mock.Setup(d => d.GetExportTenantInfoCommand()).Returns("Get-TenantInfo");
            mock.Setup(d => d.GetExportResourceAccountsCommand()).Returns("Get-ResourceAccounts");
            mock.Setup(d => d.GetExportAutoAttendantsCommand()).Returns("Get-AutoAttendants");
            mock.Setup(d => d.GetExportCallQueuesCommand()).Returns("Get-CallQueues");
            mock.Setup(d => d.GetExportSchedulesCommand()).Returns("Get-Schedules");
            mock.Setup(d => d.GetExportPhoneNumbersCommand()).Returns("Get-PhoneNumbers");
            mock.Setup(d => d.GetExportVoiceUsersCommand()).Returns("Get-VoiceUsers");
            return mock;
        }

        private static DocumentationViewModel CreateViewModel(
            ViewModelTestHarness harness,
            Mock<IDocumentationScriptBuilder> docBuilder,
            ITranslationService? translationService = null)
            => new DocumentationViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                docBuilder.Object,
                translationService: translationService);

        [Fact]
        public void Constructor_LogsPageLoaded()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();

            var vm = CreateViewModel(harness, docBuilder);

            harness.LoggingService.Verify(l => l.Log("Documentation page loaded", LogLevel.Info), Times.Once);
            Assert.Equal(string.Empty, vm.DocumentationOutput);
            Assert.False(vm.IsExporting);
        }

        [Fact]
        public async Task ExportDocumentationAsync_HappyPath_BuildsDocumentationFromParsedData()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var vm = CreateViewModel(harness, docBuilder);

            // Route each of the 7 export commands to distinct DOCDATA payloads so the parsers exercise
            // their real logic instead of just falling through the "no data" branches.
            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-TenantInfo", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "DOCDATA_TENANT:Contoso|tenant-id-1|CH|de-DE" });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-ResourceAccounts", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult
                {
                    Output = "DOCDATA_RA_START\nDOCDATA_RA:RA-One|ra1@contoso.com|obj-1|11cd3e2e-fccb-42ad-ad00-878b93575e07|+41123456789\nDOCDATA_RA_END\n" +
                             "DOCDATA_ASSOC_START\nDOCDATA_ASSOC:RA-One|obj-1|cq-1|CallQueue\nDOCDATA_ASSOC_END"
                });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-AutoAttendants", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "" });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-CallQueues", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult
                {
                    Output = "DOCDATA_CQ_START\nDOCDATA_CQ:CQ-One|cq-1|LongestIdle|20|de-DE|1|30|30|Disconnect|Disconnect\nDOCDATA_CQ_END"
                });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-Schedules", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "" });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-PhoneNumbers", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult
                {
                    Output = "DOCDATA_PHONE_START\nDOCDATA_PHONE:+41123456789|DirectRouting|obj-1|Assigned|2024-01-01|Zurich|Voice\nDOCDATA_PHONE_END"
                });

            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "Get-VoiceUsers", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "" });

            await vm.ExportDocumentationCommand.ExecuteAsync(null);

            Assert.False(vm.IsBusy);
            Assert.False(vm.IsExporting);
            Assert.Equal("Documentation exported successfully. You can copy the text above.", vm.StatusMessage);
            Assert.Contains("TEAMS PHONE SYSTEM", vm.DocumentationOutput);
            Assert.Contains("Contoso", vm.DocumentationOutput);
            Assert.Contains("tenant-id-1", vm.DocumentationOutput);
            Assert.Contains("RA-One", vm.DocumentationOutput);
            Assert.Contains("CQ-One", vm.DocumentationOutput);
            Assert.Contains("+41123456789", vm.DocumentationOutput);

            docBuilder.Verify(d => d.GetExportTenantInfoCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportResourceAccountsCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportAutoAttendantsCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportCallQueuesCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportSchedulesCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportPhoneNumbersCommand(), Times.Once);
            docBuilder.Verify(d => d.GetExportVoiceUsersCommand(), Times.Once);
        }

        [Fact]
        public async Task ExportDocumentationAsync_NoDataReturned_BuildsPlaceholderDocumentation()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            harness.SetExecutionResult(""); // every export call returns empty output
            var vm = CreateViewModel(harness, docBuilder);

            await vm.ExportDocumentationCommand.ExecuteAsync(null);

            Assert.Equal("Documentation exported successfully. You can copy the text above.", vm.StatusMessage);
            Assert.Contains("(No resource accounts found)", vm.DocumentationOutput);
            Assert.Contains("(No auto attendants found)", vm.DocumentationOutput);
            Assert.Contains("(No call queues found)", vm.DocumentationOutput);
            Assert.Contains("(No schedules found)", vm.DocumentationOutput);
            Assert.Contains("(No phone numbers found or insufficient permissions)", vm.DocumentationOutput);
            Assert.Contains("(No voice-enabled users found or insufficient permissions)", vm.DocumentationOutput);
            Assert.Contains("(No routing topology available", vm.DocumentationOutput);
        }

        [Fact]
        public async Task ExportDocumentationAsync_UsesGermanReportLabelsWhenGermanIsSelected()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            harness.SetExecutionResult("");
            harness.TranslationService.CurrentLanguage = AppLanguage.German;
            var vm = CreateViewModel(harness, docBuilder, harness.TranslationService);

            await vm.ExportDocumentationCommand.ExecuteAsync(null);

            Assert.Contains("VOLLSTÄNDIGE MANDANTENDOKUMENTATION", vm.DocumentationOutput);
            Assert.Contains("Keine Ressourcenkonten gefunden", vm.DocumentationOutput);
            Assert.DoesNotContain("COMPLETE TENANT DOCUMENTATION", vm.DocumentationOutput);
        }

        [Fact]
        public async Task ExportDocumentationAsync_SessionExpired_ShortCircuitsEachCallAndReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness, docBuilder);

            await vm.ExportDocumentationCommand.ExecuteAsync(null);

            // Every one of the 7 export calls independently hits the session pre-flight check.
            harness.ErrorHandlingService.Verify(
                e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(7));
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsExporting);
        }

        [Fact]
        public async Task CopyToClipboardAsync_NoDocumentation_SetsStatusMessageWithoutError()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var vm = CreateViewModel(harness, docBuilder);

            await vm.CopyToClipboardCommand.ExecuteAsync(null);

            Assert.Equal("No documentation to copy. Export first.", vm.StatusMessage);
        }

        [Fact]
        public async Task CopyToClipboardAsync_WithDocumentation_NoAvaloniaApp_ReportsClipboardUnavailable()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var vm = CreateViewModel(harness, docBuilder);
            vm.DocumentationOutput = "some documentation text";

            await vm.CopyToClipboardCommand.ExecuteAsync(null);

            // No Avalonia Application is running in the unit test host, so Application.Current is null
            // and the clipboard branch cannot succeed.
            Assert.Equal("Clipboard not available.", vm.StatusMessage);
        }
    }
}
