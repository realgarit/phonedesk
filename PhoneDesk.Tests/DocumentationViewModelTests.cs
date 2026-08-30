using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;
using PhoneDesk.Portability;
using PhoneDesk.Topology;

namespace PhoneDesk.Tests
{
    public class DocumentationViewModelTests
    {
        private static TenantTopology CreateTopology(string languageId, bool includeSecondResourceAccount)
        {
            var resourceAccounts = new List<TopologyResourceAccount>
            {
                new("Main RA", "main@contoso.example", "ra-1", "+41440000000", ResourceAccountKind.AutoAttendant, true),
            };
            if (includeSecondResourceAccount)
            {
                resourceAccounts.Add(new TopologyResourceAccount(
                    "Queue RA", "queue@contoso.example", "ra-2", null, ResourceAccountKind.CallQueue, true));
            }

            return new TenantTopology(
                new[]
                {
                    new TopologyAutoAttendant(
                        "Main AA", "aa-1", languageId, "Europe/Zurich",
                        new[] { "ra-1" }, Array.Empty<string>(), new[] { "cq-1" }),
                },
                new[]
                {
                    new TopologyCallQueue(
                        "Support CQ", "cq-1", "Attendant", 30,
                        new[] { "agent-1" }, Array.Empty<string>(),
                        includeSecondResourceAccount ? new[] { "ra-2" } : Array.Empty<string>()),
                },
                resourceAccounts,
                Array.Empty<TopologyGroup>(),
                Array.Empty<OrphanFinding>(),
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        }

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
            ITranslationService? translationService = null,
            ITenantAsCodeService? tenantAsCodeService = null,
            IPortabilityFileService? portabilityFileService = null,
            ITenantTopologyCache? topologyCache = null)
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
                translationService: translationService,
                tenantAsCodeService: tenantAsCodeService,
                portabilityFileService: portabilityFileService,
                topologyCache: topologyCache);

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
        public async Task ExportTopologySnapshotUsesOnlyTheCachedTopology()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var service = new TenantAsCodeService();
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.SaveJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("C:\\topology.json");
            var cache = new TenantTopologyCache();
            cache.Set(CreateTopology(languageId: "en-US", includeSecondResourceAccount: true));
            var vm = CreateViewModel(harness, docBuilder, tenantAsCodeService: service,
                portabilityFileService: files.Object, topologyCache: cache);

            await vm.ExportTopologySnapshotCommand.ExecuteAsync(null);

            files.Verify(file => file.SaveJsonAsync(
                It.IsAny<string>(),
                "phonedesk-topology-20260830T120000Z.json",
                It.Is<string>(json =>
                    json.Contains("\"kind\": \"phonedesk.topology\"", StringComparison.Ordinal) &&
                    json.Contains("\"identity\": \"aa-1\"", StringComparison.Ordinal))), Times.Once);
            harness.PowerShellContextService.Verify(
                context => context.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CompareTopologySnapshotShowsAddedRemovedAndPropertyChangesWithoutTenantCalls()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var service = new TenantAsCodeService();
            var savedTopology = CreateTopology(languageId: "de-DE", includeSecondResourceAccount: false);
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile(
                    "baseline.json",
                    "C:\\baseline.json",
                    service.SerializeTopology(savedTopology)));
            var cache = new TenantTopologyCache();
            cache.Set(CreateTopology(languageId: "en-US", includeSecondResourceAccount: true));
            var vm = CreateViewModel(harness, docBuilder, tenantAsCodeService: service,
                portabilityFileService: files.Object, topologyCache: cache);

            await vm.CompareTopologySnapshotCommand.ExecuteAsync(null);

            Assert.True(vm.ShowTopologyDrift);
            Assert.Equal("baseline.json", vm.ComparedTopologyFileName);
            Assert.Contains(vm.TopologyDriftEntries, item =>
                item.Kind == TopologyDriftKind.Changed &&
                item.ObjectId == "aa-1" && item.PropertyName == "languageId");
            Assert.Contains(vm.TopologyDriftEntries, item =>
                item.Kind == TopologyDriftKind.Added && item.ObjectId == "ra-2");
            harness.PowerShellContextService.Verify(
                context => context.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public void VisibleTopologyDriftLabelsRefreshWhenLanguageChanges()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(
                harness,
                CreateDocBuilderMock(),
                harness.TranslationService);
            vm.SetTopologyDriftForDisplay(
                "baseline.json",
                new[]
                {
                    new TopologyDriftEntry(
                        TopologyDriftKind.Added,
                        "resourceAccount",
                        "ra-2",
                        "Queue RA",
                        null,
                        null,
                        null),
                });

            Assert.Equal("Added", Assert.Single(vm.TopologyDriftEntries).ChangeLabel);

            harness.TranslationService.CurrentLanguage = AppLanguage.German;

            Assert.Equal("Hinzugefügt", Assert.Single(vm.TopologyDriftEntries).ChangeLabel);
        }

        [Fact]
        public async Task SnapshotActionsRequireTopologyLoadedOnDashboard()
        {
            var harness = new ViewModelTestHarness();
            var files = new Mock<IPortabilityFileService>();
            var vm = CreateViewModel(
                harness,
                CreateDocBuilderMock(),
                tenantAsCodeService: new TenantAsCodeService(),
                portabilityFileService: files.Object,
                topologyCache: new TenantTopologyCache());

            await vm.ExportTopologySnapshotCommand.ExecuteAsync(null);
            await vm.CompareTopologySnapshotCommand.ExecuteAsync(null);

            Assert.Contains("Dashboard", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            files.VerifyNoOtherCalls();
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
