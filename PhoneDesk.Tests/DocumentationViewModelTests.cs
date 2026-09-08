using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;
using PhoneDesk.Portability;
using PhoneDesk.Topology;
using PhoneDesk.HealthChecks;

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
            ITranslationService? translationService = null,
            ITenantAsCodeService? tenantAsCodeService = null,
            IPortabilityFileService? portabilityFileService = null,
            ITenantDocumentationSnapshotParser? snapshotParser = null,
            ITenantTopologyAssembler? topologyAssembler = null,
            ITenantHealthCheckCache? healthCheckCache = null)
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
                snapshotParser: snapshotParser,
                topologyAssembler: topologyAssembler,
                healthCheckCache: healthCheckCache);

        private static void SetupSnapshotOutputs(
            ViewModelTestHarness harness,
            TenantDocumentationRawData rawData,
            string topologyRaw)
        {
            harness.PowerShellCommandService
                .Setup(service => service.GetRetrieveTenantTopologyCommand())
                .Returns("Get-Topology");
            SetupCommandOutput(harness, "Get-TenantInfo", rawData.Tenant);
            SetupCommandOutput(harness, "Get-ResourceAccounts", rawData.ResourceAccounts);
            SetupCommandOutput(harness, "Get-AutoAttendants", rawData.AutoAttendants);
            SetupCommandOutput(harness, "Get-CallQueues", rawData.CallQueues);
            SetupCommandOutput(harness, "Get-Schedules", rawData.Schedules);
            SetupCommandOutput(harness, "Get-PhoneNumbers", rawData.PhoneNumbers);
            SetupCommandOutput(harness, "Get-VoiceUsers", rawData.VoiceUsers);
            SetupCommandOutput(harness, "Get-Topology", topologyRaw);
        }

        private static void SetupCommandOutput(
            ViewModelTestHarness harness,
            string command,
            string output)
        {
            harness.PowerShellContextService
                .Setup(service => service.ExecuteCommandWithDetailsAsync(
                    command,
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = output });
        }

        private const string CompleteTopologyRaw =
            "TOPRA: Reception RA|reception@contoso.example|ra-1|+41440000000|CallQueue|True\n" +
            "TOPAA: Reception|aa-1|de-DE|Europe/Zurich|ra-1|schedule-1|cq-1\n" +
            "TOPCQ: Support|cq-1|LongestIdle|20|agent-1|group-1|ra-1\n" +
            "TOPGRP: Support group|group-1|support|Support agents\n" +
            "SUCCESS: Tenant topology retrieved";

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
        public async Task ExportTopologySnapshotQueriesLiveTenantAndIncludesFullReportInventory()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var service = new TenantAsCodeService();
            SetupSnapshotOutputs(
                harness,
                TenantDocumentationSnapshotParserTests.CreateRawData(),
                CompleteTopologyRaw);
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.SaveJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("C:\\topology.json");
            var vm = CreateViewModel(harness, docBuilder, tenantAsCodeService: service,
                portabilityFileService: files.Object,
                snapshotParser: new TenantDocumentationSnapshotParser(),
                topologyAssembler: new TenantTopologyAssembler());

            await vm.ExportTopologySnapshotCommand.ExecuteAsync(null);

            files.Verify(file => file.SaveJsonAsync(
                It.IsAny<string>(),
                It.Is<string>(name => name.StartsWith("phonedesk-topology-", StringComparison.Ordinal) && name.EndsWith("Z.json", StringComparison.Ordinal)),
                It.Is<string>(json =>
                    json.Contains("\"schemaVersion\": 2", StringComparison.Ordinal) &&
                    json.Contains("\"kind\": \"phonedesk.topology\"", StringComparison.Ordinal) &&
                    json.Contains("\"documentation\"", StringComparison.Ordinal) &&
                    json.Contains("\"phoneNumbers\"", StringComparison.Ordinal) &&
                    json.Contains("\"voiceUsers\"", StringComparison.Ordinal) &&
                    json.Contains("\"schedules\"", StringComparison.Ordinal))), Times.Once);
            harness.PowerShellContextService.Verify(
                context => context.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Exactly(8));
        }

        [Fact]
        public async Task CompareTopologySnapshotUsesNewLiveReadAndShowsInventoryPropertyChanges()
        {
            var harness = new ViewModelTestHarness();
            var docBuilder = CreateDocBuilderMock();
            var service = new TenantAsCodeService();
            var parser = new TenantDocumentationSnapshotParser();
            var assembler = new TenantTopologyAssembler();
            var savedRaw = TenantDocumentationSnapshotParserTests.CreateRawData();
            var savedTopology = service.CreateTopologySnapshot(
                assembler.Assemble(CompleteTopologyRaw, new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
                parser.Parse(savedRaw));
            var liveRaw = savedRaw with
            {
                PhoneNumbers = savedRaw.PhoneNumbers.Replace("|Zurich|", "|Bern|", StringComparison.Ordinal)
            };
            SetupSnapshotOutputs(harness, liveRaw, CompleteTopologyRaw);
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile(
                    "baseline.json",
                    "C:\\baseline.json",
                    service.SerializeTopology(savedTopology)));
            var vm = CreateViewModel(harness, docBuilder, tenantAsCodeService: service,
                portabilityFileService: files.Object,
                snapshotParser: parser,
                topologyAssembler: assembler);

            await vm.CompareTopologySnapshotCommand.ExecuteAsync(null);

            Assert.True(vm.ShowTopologyDrift);
            Assert.Equal("baseline.json", vm.ComparedTopologyFileName);
            Assert.Contains(vm.TopologyDriftEntries, item =>
                item.Kind == TopologyDriftKind.Changed &&
                item.ObjectType == "phoneNumber" &&
                item.ObjectId == "+41440000000" &&
                item.PropertyName == "city" &&
                item.SnapshotValue == "Zurich" &&
                item.LiveValue == "Bern");
            harness.PowerShellContextService.Verify(
                context => context.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Exactly(8));
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
        public async Task SnapshotExportFailsClosedWhenAnyLiveDatasetIsIncomplete()
        {
            var harness = new ViewModelTestHarness();
            var raw = TenantDocumentationSnapshotParserTests.CreateRawData() with
            {
                PhoneNumbers =
                    "WARNING: Could not export phone numbers (insufficient permissions)\n" +
                    "DOCDATA_PHONE_START\nDOCDATA_PHONE_END"
            };
            SetupSnapshotOutputs(harness, raw, CompleteTopologyRaw);
            var files = new Mock<IPortabilityFileService>();
            var vm = CreateViewModel(
                harness,
                CreateDocBuilderMock(),
                tenantAsCodeService: new TenantAsCodeService(),
                portabilityFileService: files.Object,
                snapshotParser: new TenantDocumentationSnapshotParser(),
                topologyAssembler: new TenantTopologyAssembler());

            await vm.ExportTopologySnapshotCommand.ExecuteAsync(null);

            Assert.Contains("incomplete", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WARNING", vm.StatusMessage, StringComparison.Ordinal);
            files.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task CompareRejectsInvalidSnapshotBeforeQueryingTheTenant()
        {
            var harness = new ViewModelTestHarness();
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile("invalid.json", "C:\\invalid.json", "{ invalid"));
            var vm = CreateViewModel(
                harness,
                CreateDocBuilderMock(),
                tenantAsCodeService: new TenantAsCodeService(),
                portabilityFileService: files.Object,
                snapshotParser: new TenantDocumentationSnapshotParser(),
                topologyAssembler: new TenantTopologyAssembler());

            await vm.CompareTopologySnapshotCommand.ExecuteAsync(null);

            Assert.Contains("invalid", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            harness.PowerShellContextService.Verify(
                context => context.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
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
        public async Task ExportDocumentationIncludesLatestHealthFindingsForSameTenant()
        {
            var harness = new ViewModelTestHarness();
            harness.SessionManager.SetupGet(manager => manager.TenantId).Returns("tenant-1");
            harness.SetExecutionResult(string.Empty);
            var cache = new TenantHealthCheckCache();
            cache.Set(new TenantHealthCheckResult(
                "tenant-1",
                new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
                new[]
                {
                    new TenantHealthFinding(
                        "call-queue-without-agents:empty:cq-1",
                        TenantHealthRuleCatalog.CallQueueWithoutAgents,
                        TenantHealthSeverity.Error,
                        "callQueue",
                        "cq-1",
                        "Empty support",
                        new LocalizedHealthText("Queue cannot ring a person.", "Warteschleife kann keine Person anrufen."),
                        new LocalizedHealthText("Add an agent.", "Fügen Sie einen Agenten hinzu."),
                        ConstantsService.Pages.CallQueues),
                },
                SuppressedCount: 1,
                DisabledRuleCount: 2));
            var vm = CreateViewModel(
                harness,
                CreateDocBuilderMock(),
                healthCheckCache: cache);

            await vm.ExportDocumentationCommand.ExecuteAsync(null);

            Assert.Contains("APPENDIX — TENANT HEALTH CHECK", vm.DocumentationOutput);
            Assert.Contains("Empty support", vm.DocumentationOutput);
            Assert.Contains("Queue cannot ring a person.", vm.DocumentationOutput);
            Assert.Contains("Add an agent.", vm.DocumentationOutput);
            Assert.Contains("Suppressed: 1", vm.DocumentationOutput);
            Assert.Contains("Disabled rules: 2", vm.DocumentationOutput);
            var titleIndex = vm.DocumentationOutput.IndexOf("TEAMS PHONE SYSTEM", StringComparison.Ordinal);
            var appendixIndex = vm.DocumentationOutput.IndexOf("APPENDIX — TENANT HEALTH CHECK", StringComparison.Ordinal);
            var endIndex = vm.DocumentationOutput.IndexOf("END OF DOCUMENTATION", StringComparison.Ordinal);
            Assert.True(titleIndex >= 0 && titleIndex < appendixIndex);
            Assert.True(appendixIndex < endIndex);
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
