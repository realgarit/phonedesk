using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Covers <see cref="BulkOperationsViewModel"/>: CSV parsing edge cases (BOM, quoted commas, missing
    /// columns, empty content) driven through <c>ParseCsvCommand</c>, and the happy/error/session-expiry
    /// paths of <c>ExecuteAllCommand</c>.
    ///
    /// <see cref="BulkOperationsScriptBuilder"/> is a concrete class with two lightweight interface
    /// dependencies (<see cref="IPowerShellCommandService"/>, <see cref="IPowerShellSanitizationService"/>),
    /// both mocked here (a verbatim sanitizer, matching the pattern in BulkOperationsSnapshotTests) so the
    /// builder itself runs for real — this is what actually parses the CSV.
    /// </summary>
    public class BulkOperationsViewModelTests
    {
        private const string TemplateHeader =
            "Customer,CustomerGroupName,MsFallbackDomain,RaaAnrName,LanguageId,TimeZoneId,UsageLocation,PhoneNumber,PhoneNumberType,OpeningHours1Start,OpeningHours1End,OpeningHours2Start,OpeningHours2End";

        private const string TemplateDataRow =
            "contoso,hauptnummer,@contoso.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234567,DirectRouting,08:00,12:00,13:00,17:00";

        private static IPowerShellSanitizationService VerbatimSanitizer()
        {
            var mock = new Mock<IPowerShellSanitizationService>();
            mock.Setup(s => s.SanitizeString(It.IsAny<string>())).Returns<string>(x => x);
            mock.Setup(s => s.SanitizeIdentifier(It.IsAny<string>())).Returns<string>(x => x);
            return mock.Object;
        }

        private static BulkOperationsViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new BulkOperationsViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                new BulkOperationsScriptBuilder(harness.PowerShellCommandService.Object, VerbatimSanitizer()));

        // ── CSV parsing edge cases ──────────────────────────────────────────

        [Fact]
        public void ParseCsv_Utf8Bom_StripsBom_FirstHeaderMatchesCorrectly()
        {
            // A UTF-8 BOM prefixed onto the header line must not become part of the first header
            // token ("﻿Customer"), which would otherwise make BulkOperationsScriptBuilder.GetField's
            // lookup for "Customer" miss and silently drop that column. The BOM should be stripped
            // before header parsing so the first column matches and parses normally.
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = "﻿" + TemplateHeader + "\n" + TemplateDataRow;

            vm.ParseCsvCommand.Execute(null);

            Assert.Single(vm.ParsedEntries);
            var entry = vm.ParsedEntries[0];
            Assert.Equal("contoso", entry.Customer);
            Assert.Equal("hauptnummer", entry.GroupName);
            Assert.Equal("+41441234567", entry.PhoneNumber);
            Assert.Equal("de-DE", entry.Language);
            Assert.Contains("Parsed 1 entries", vm.StatusMessage);
        }

        [Fact]
        public void ParseCsv_QuotedValueContainingComma_IsNotSplitIntoExtraColumns()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var row = "\"Doe, Inc\",hauptnummer,@contoso.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234567,DirectRouting,08:00,12:00,13:00,17:00";
            vm.CsvContent = TemplateHeader + "\n" + row;

            vm.ParseCsvCommand.Execute(null);

            Assert.Single(vm.ParsedEntries);
            var entry = vm.ParsedEntries[0];
            // The comma inside quotes must stay part of the Customer field, not spill into GroupName.
            Assert.Equal("Doe, Inc", entry.Customer);
            Assert.Equal("hauptnummer", entry.GroupName);
            Assert.Equal("+41441234567", entry.PhoneNumber);
        }

        [Fact]
        public void ParseCsv_RowMissingTrailingColumns_IsStillAddedWithEmptyMissingFields()
        {
            // BulkOperationsScriptBuilder.GetField returns string.Empty when the field index is out of
            // range for a short row, rather than throwing or skipping the row entirely.
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var shortRow = "contoso,hauptnummer,@contoso.onmicrosoft.com,haupt,de-DE"; // only 5 of 13 columns
            vm.CsvContent = TemplateHeader + "\n" + shortRow;

            vm.ParseCsvCommand.Execute(null);

            Assert.Single(vm.ParsedEntries);
            var entry = vm.ParsedEntries[0];
            Assert.Equal("contoso", entry.Customer);
            Assert.Equal("hauptnummer", entry.GroupName);
            Assert.Equal("de-DE", entry.Language);
            // PhoneNumber maps to the "PhoneNumber" column (index 7), which this row doesn't have.
            Assert.Equal(string.Empty, entry.PhoneNumber);
            Assert.Contains("Parsed 1 entries", vm.StatusMessage);
        }

        [Fact]
        public void ParseCsv_EmptyContent_DoesNotThrow_LeavesParsedEntriesEmpty()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = string.Empty;

            var exception = Record.Exception(() => vm.ParseCsvCommand.Execute(null));

            Assert.Null(exception);
            Assert.Empty(vm.ParsedEntries);
            Assert.Equal("No CSV content to parse.", vm.StatusMessage);
        }

        [Fact]
        public void ParseCsv_WhitespaceOnlyContent_DoesNotThrow_LeavesParsedEntriesEmpty()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = "   \n\t  ";

            var exception = Record.Exception(() => vm.ParseCsvCommand.Execute(null));

            Assert.Null(exception);
            Assert.Empty(vm.ParsedEntries);
            Assert.Equal("No CSV content to parse.", vm.StatusMessage);
        }

        [Fact]
        public void ParseCsv_HeaderOnly_NoDataRows_ProducesEmptyParsedEntriesWithStatus()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = TemplateHeader;

            var exception = Record.Exception(() => vm.ParseCsvCommand.Execute(null));

            Assert.Null(exception);
            Assert.Empty(vm.ParsedEntries);
            Assert.Equal("No valid entries found in the CSV. Check the format.", vm.StatusMessage);
        }

        // ── ExecuteAll command gating and PowerShell paths ─────────────────

        [Fact]
        public async Task ExecuteAllAsync_NoParsedEntries_DoesNotCallPowerShell()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.ExecuteAllCommand.ExecuteAsync(null);

            Assert.Equal("No entries to execute. Parse CSV first.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAllAsync_HappyPath_MarksAllEntriesProcessed()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = TemplateHeader + "\n" + TemplateDataRow;
            vm.ParseCsvCommand.Execute(null);
            harness.SetExecutionResult("SUCCESS: all entries processed");

            await vm.ExecuteAllCommand.ExecuteAsync(null);

            Assert.Contains("completed successfully", vm.StatusMessage);
            Assert.Equal(1, vm.TotalCount);
            Assert.Equal(1, vm.ProcessedCount);
            Assert.False(vm.IsExecuting);
        }

        [Fact]
        public async Task ExecuteAllAsync_ErrorPath_ReportsErrorsInStatusAndLog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = TemplateHeader + "\n" + TemplateDataRow;
            vm.ParseCsvCommand.Execute(null);
            harness.SetExecutionResult("ERROR: something went wrong", hadErrors: true);

            await vm.ExecuteAllCommand.ExecuteAsync(null);

            Assert.Contains("completed with errors", vm.StatusMessage);
            Assert.Contains("ERROR: something went wrong", vm.ExecutionLog);
            Assert.False(vm.IsExecuting);
        }

        [Fact]
        public async Task ExecuteAllAsync_SessionExpired_ReportsErrorsWithoutExecutingScript()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = TemplateHeader + "\n" + TemplateDataRow;
            vm.ParseCsvCommand.Execute(null);
            harness.SetSessionExpired();

            await vm.ExecuteAllCommand.ExecuteAsync(null);

            Assert.Contains("completed with errors", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task ExecuteAllAsync_UserCancelsConfirmation_DoesNotExecute()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.CsvContent = TemplateHeader + "\n" + TemplateDataRow;
            vm.ParseCsvCommand.Execute(null);
            harness.DialogService
                .Setup(d => d.ShowConfirmationWithPreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(false);

            await vm.ExecuteAllCommand.ExecuteAsync(null);

            Assert.Equal("Bulk execution cancelled.", vm.StatusMessage);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public void GenerateTemplateCommand_PopulatesCsvContentFromBuilder()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.GenerateTemplateCommand.Execute(null);

            Assert.Contains(TemplateHeader, vm.CsvContent);
            Assert.Contains("CSV template generated", vm.StatusMessage);
        }
    }
}
