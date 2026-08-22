using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Models;
using PhoneDesk.Planning;
using PhoneDesk.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    public partial class BulkOperationsViewModel : ViewModelBase
    {
        private readonly BulkOperationsScriptBuilder _bulkBuilder;
        private readonly IDryRunPlanBuilder? _planBuilder;
        private readonly IDryRunPlanExporter? _planExporter;

        /// <summary>
        /// The most recently generated dry-run plan for the parsed CSV entries. Null until the operator
        /// generates a preview. Generating it performs no tenant mutation and executes no PowerShell.
        /// </summary>
        [ObservableProperty]
        private DryRunPlan? _plan;

        /// <summary>
        /// When true, executing bulk operations skips entries that failed validation/preflight instead of
        /// blocking the whole run. Off by default: nothing executes while any row is invalid.
        /// </summary>
        [ObservableProperty]
        private bool _skipInvalidRows;

        [ObservableProperty]
        private string _csvContent = string.Empty;

        [ObservableProperty]
        private string _scriptPreview = string.Empty;

        [ObservableProperty]
        private ObservableCollection<BulkEntryPreview> _parsedEntries = new();

        [ObservableProperty]
        private bool _isExecuting = false;

        [ObservableProperty]
        private int _processedCount = 0;

        [ObservableProperty]
        private int _totalCount = 0;

        [ObservableProperty]
        private string _executionLog = string.Empty;

        public BulkOperationsViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            BulkOperationsScriptBuilder bulkBuilder,
            IDryRunPlanBuilder? planBuilder = null,
            IDryRunPlanExporter? planExporter = null,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _bulkBuilder = bulkBuilder;
            _planBuilder = planBuilder;
            _planExporter = planExporter;
            LogLocalized(
                UiTextKey.BulkOperationsPageLoadedLog,
                "Bulk operations page loaded",
                LogLevel.Info);
        }

        [RelayCommand]
        private void GenerateTemplate()
        {
            CsvContent = _bulkBuilder.GenerateCsvTemplate();
            StatusMessage = GetText(
                UiTextKey.BulkOperationsTemplateGeneratedStatus,
                "CSV template generated. Edit the data and click 'Parse CSV'.");
            LogLocalized(
                UiTextKey.BulkOperationsTemplateGeneratedLog,
                "Bulk CSV template generated",
                LogLevel.Info);
        }

        [RelayCommand]
        private async Task ImportCsvFileAsync()
        {
            try
            {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

                if (topLevel == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsFilePickerUnavailable,
                        "Cannot open the file picker.");
                    return;
                }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Import CSV File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } },
                        new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                    }
                });

                if (files.Count > 0)
                {
                    await using var stream = await files[0].OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    CsvContent = await reader.ReadToEndAsync();
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsCsvLoadedStatus,
                        "CSV file loaded: {name}",
                        new Dictionary<string, object?> { ["name"] = files[0].Name });
                    LogLocalized(
                        UiTextKey.BulkOperationsCsvLoadedLog,
                        "Bulk CSV imported from file: {name}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = files[0].Name });
                    ParseCsvContent();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsImportFailedStatus,
                    "Failed to import: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                LogLocalized(
                    UiTextKey.BulkOperationsImportFailedLog,
                    "Bulk CSV import failed: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void ParseCsv()
        {
            ParseCsvContent();
        }

        private void ParseCsvContent()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CsvContent))
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsNoCsvContentError,
                        "No CSV content to parse.");
                    return;
                }

                var entries = _bulkBuilder.ParseCsv(CsvContent);
                ParsedEntries.Clear();

                foreach (var vars in entries)
                {
                    ParsedEntries.Add(new BulkEntryPreview
                    {
                        Customer = vars.Customer,
                        GroupName = vars.CustomerGroupName,
                        M365Group = vars.M365Group,
                        CqDisplayName = vars.CqDisplayName,
                        AaDisplayName = vars.AaDisplayName,
                        PhoneNumber = vars.RaaAnr,
                        Language = vars.LanguageId,
                        Variables = vars
                    });
                }

                if (ParsedEntries.Count > 0)
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsParsedEntriesStatus,
                        "Parsed {count} entries. Click 'Preview Script' to review, then 'Execute All'.",
                        new Dictionary<string, object?> { ["count"] = ParsedEntries.Count });
                    LogLocalized(
                        UiTextKey.BulkOperationsParsedEntriesLog,
                        "Bulk CSV parsed: {count} entries",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["count"] = ParsedEntries.Count });
                }
                else
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsNoValidEntriesError,
                        "No valid entries found in the CSV. Check the format.");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsParseErrorStatus,
                    "Parse error: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                LogLocalized(
                    UiTextKey.BulkOperationsParseErrorLog,
                    "Bulk CSV parse error: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void PreviewScript()
        {
            if (ParsedEntries.Count == 0)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsNoEntriesToPreviewError,
                    "No entries to preview. Parse CSV first.");
                return;
            }

            var entries = new System.Collections.Generic.List<PhoneManagerVariables>();
            foreach (var entry in ParsedEntries)
            {
                entries.Add(entry.Variables);
            }

            ScriptPreview = _bulkBuilder.GenerateBulkScript(entries);
            StatusMessage = GetText(
                UiTextKey.BulkOperationsScriptPreviewGeneratedStatus,
                "Script preview generated for {count} entries.",
                new Dictionary<string, object?> { ["count"] = entries.Count });
        }

        private List<PhoneManagerVariables> CollectParsedVariables() =>
            ParsedEntries.Select(e => e.Variables).ToList();

        /// <summary>
        /// Builds a read-only dry-run plan for the parsed CSV: a per-row list of the objects that would be
        /// created with resolved names/UPNs/number, per-row validation errors, and intra-plan preflight
        /// (duplicate group/UPN/number across rows). Performs no tenant mutation and executes no PowerShell.
        /// </summary>
        [RelayCommand]
        private void GeneratePlan()
        {
            if (_planBuilder == null)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsPlanPreviewUnavailable,
                    "Plan preview is unavailable.");
                return;
            }

            if (ParsedEntries.Count == 0)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsNoEntriesToPlanError,
                    "No entries to plan. Parse CSV first.");
                return;
            }

            Plan = _planBuilder.BuildBulkPlan(CollectParsedVariables());
            StatusMessage = Plan.InvalidEntryCount > 0
                ? GetText(
                    UiTextKey.BulkOperationsPlanGeneratedWithIssues,
                    "Plan generated: {valid} valid, {invalid} invalid of {rows} rows. Fix the issues or enable 'Skip invalid rows'.",
                    new Dictionary<string, object?> { ["valid"] = Plan.ValidEntryCount, ["invalid"] = Plan.InvalidEntryCount, ["rows"] = Plan.EntryCount })
                : GetText(
                    UiTextKey.BulkOperationsPlanGenerated,
                    "Plan generated: {rows} rows, {objects} objects would be created or changed.",
                    new Dictionary<string, object?> { ["rows"] = Plan.EntryCount, ["objects"] = Plan.TotalObjectCount });
            LogLocalized(
                UiTextKey.BulkOperationsPlanGeneratedLog,
                "Bulk dry-run plan generated: {rows} rows ({invalid} invalid)",
                LogLevel.Info,
                new Dictionary<string, object?> { ["rows"] = Plan.EntryCount, ["invalid"] = Plan.InvalidEntryCount });
        }

        [RelayCommand]
        private Task ExportPlanJsonAsync() => ExportPlanAsync(asJson: true);

        [RelayCommand]
        private Task ExportPlanCsvAsync() => ExportPlanAsync(asJson: false);

        private async Task ExportPlanAsync(bool asJson)
        {
            if (_planBuilder == null || _planExporter == null)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsPlanExportUnavailable,
                    "Plan export is unavailable.");
                return;
            }

            if (ParsedEntries.Count == 0)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsNoEntriesToExportError,
                    "No entries to export. Parse CSV first.");
                return;
            }

            Plan ??= _planBuilder.BuildBulkPlan(CollectParsedVariables());

            var content = asJson ? _planExporter.ToJson(Plan) : _planExporter.ToCsv(Plan);
            var extension = asJson ? "json" : "csv";
            var saved = await DryRunPlanExportHelper.SavePlanAsync(content, $"bulk-dry-run-plan.{extension}", extension);
            if (saved != null)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsPlanExportedStatus,
                    "Plan exported to {path}",
                    new Dictionary<string, object?> { ["path"] = saved });
                LogLocalized(
                    UiTextKey.BulkOperationsPlanExportedLog,
                    "Bulk dry-run plan exported to: {path}",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["path"] = saved });
            }
            else
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsPlanExportCancelled,
                    "Plan export cancelled.");
            }
        }

        [RelayCommand]
        private async Task ExecuteAllAsync()
        {
            if (ParsedEntries.Count == 0)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsNoEntriesToExecuteError,
                    "No entries to execute. Parse CSV first.");
                return;
            }

            var entries = CollectParsedVariables();

            // Dry-run gate: validate the whole batch up front. Nothing executes while any row is invalid
            // unless the operator explicitly opts into skipping invalid rows, in which case only the valid
            // rows execute. This selects which entries run; it never alters the frozen builder's output for
            // a given entry, so execution remains byte-identical.
            if (_planBuilder != null)
            {
                Plan = _planBuilder.BuildBulkPlan(entries);

                if (Plan.InvalidEntryCount > 0)
                {
                    if (!SkipInvalidRows)
                    {
                        StatusMessage = GetText(
                            UiTextKey.BulkOperationsInvalidRowsBlockedStatus,
                            "{invalid} of {rows} rows are invalid. Fix them, or enable 'Skip invalid rows' to run only the valid rows.",
                            new Dictionary<string, object?> { ["invalid"] = Plan.InvalidEntryCount, ["rows"] = Plan.EntryCount });
                        LogLocalized(
                            UiTextKey.BulkOperationsInvalidRowsBlockedLog,
                            "Bulk execution blocked: {invalid} invalid rows and skip-invalid is off",
                            LogLevel.Warning,
                            new Dictionary<string, object?> { ["invalid"] = Plan.InvalidEntryCount });
                        return;
                    }

                    var validEntries = Plan.ValidEntries.Select(e => entries[e.RowNumber - 1]).ToList();
                    if (validEntries.Count == 0)
                    {
                        StatusMessage = GetText(
                            UiTextKey.BulkOperationsAllRowsInvalidStatus,
                            "All rows are invalid. Nothing to execute.");
                        return;
                    }

                    LogLocalized(
                        UiTextKey.BulkOperationsInvalidRowsSkippedLog,
                        "Bulk execution skipping {invalid} invalid rows; running {valid} valid rows",
                        LogLevel.Warning,
                        new Dictionary<string, object?> { ["invalid"] = Plan.InvalidEntryCount, ["valid"] = validEntries.Count });
                    entries = validEntries;
                }
            }

            var bulkScript = _bulkBuilder.GenerateBulkScript(entries);

            // Show confirmation with preview
            if (_dialogService != null)
            {
                var confirmed = await _dialogService.ShowConfirmationWithPreviewAsync(
                    GetText(
                        UiTextKey.BulkOperationsExecuteTitle,
                        "Execute bulk operations"),
                    GetText(
                        UiTextKey.BulkOperationsExecuteMessage,
                        "This executes {count} complete phone system setups. This action creates M365 groups, resource accounts, call queues, and auto attendants for all entries.",
                        new Dictionary<string, object?> { ["count"] = entries.Count }),
                    bulkScript);

                if (!confirmed)
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsExecuteCancelledStatus,
                        "Bulk execution cancelled.");
                    return;
                }
            }

            try
            {
                IsBusy = true;
                IsExecuting = true;
                TotalCount = entries.Count;
                ProcessedCount = 0;
                var log = new StringBuilder();

                StatusMessage = GetText(
                    UiTextKey.BulkOperationsExecutionStartedStatus,
                    "Executing bulk operations for {count} entries...",
                    new Dictionary<string, object?> { ["count"] = entries.Count });
                LogLocalized(
                    UiTextKey.BulkOperationsExecutionStartedLog,
                    "Bulk execution started: {count} entries",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["count"] = entries.Count });

                // Execute the entire script as one batch
                var result = await ExecutePowerShellCommandAsync(bulkScript, "BulkOperations");

                if (!string.IsNullOrEmpty(result.Value))
                {
                    log.AppendLine(result.Value);

                    if (result.HasErrorMarker)
                    {
                        StatusMessage = GetText(
                            UiTextKey.BulkOperationsExecutionCompletedWithErrorsStatus,
                            "Bulk execution completed with errors. Check the log below.");
                        LogLocalized(
                            UiTextKey.BulkOperationsExecutionCompletedWithErrorsLog,
                            "Bulk execution completed with errors",
                            LogLevel.Warning);
                    }
                    else
                    {
                        StatusMessage = GetText(
                            UiTextKey.BulkOperationsExecutionCompletedSuccessStatus,
                            "Bulk execution completed successfully for {count} entries.",
                            new Dictionary<string, object?> { ["count"] = entries.Count });
                        LogLocalized(
                            UiTextKey.BulkOperationsExecutionCompletedSuccessLog,
                            "Bulk execution completed successfully: {count} entries",
                            LogLevel.Info,
                            new Dictionary<string, object?> { ["count"] = entries.Count });
                    }
                }
                else
                {
                    log.AppendLine(GetText(
                        UiTextKey.BulkOperationsExecutionNoOutputLog,
                        "Script executed (no output returned)."));
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsExecutionCompletedStatus,
                        "Bulk execution completed for {count} entries.",
                        new Dictionary<string, object?> { ["count"] = entries.Count });
                }

                ProcessedCount = TotalCount;
                ExecutionLog = log.ToString();
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsExecutionFailedStatus,
                    "Bulk execution failed: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                ExecutionLog += Environment.NewLine + GetText(
                    UiTextKey.BulkOperationsExecutionFatalErrorPrefix,
                    "FATAL ERROR: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                LogLocalized(
                    UiTextKey.BulkOperationsExecutionFailedLog,
                    "Bulk execution exception: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
                IsExecuting = false;
            }
        }

        [RelayCommand]
        private async Task ExportTemplateFileAsync()
        {
            try
            {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

                if (topLevel == null)
                {
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsFilePickerUnavailable,
                        "Cannot open the file picker.");
                    return;
                }

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Export CSV Template",
                    DefaultExtension = "csv",
                    SuggestedFileName = "teams-phone-bulk-template.csv",
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } }
                    }
                });

                if (file != null)
                {
                    var template = _bulkBuilder.GenerateCsvTemplate();
                    await using var stream = await file.OpenWriteAsync();
                    await using var writer = new StreamWriter(stream, Encoding.UTF8);
                    await writer.WriteAsync(template);
                    StatusMessage = GetText(
                        UiTextKey.BulkOperationsTemplateExportedStatus,
                        "Template exported to {name}",
                        new Dictionary<string, object?> { ["name"] = file.Name });
                    LogLocalized(
                        UiTextKey.BulkOperationsTemplateExportedLog,
                        "Bulk CSV template exported to: {name}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["name"] = file.Name });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.BulkOperationsTemplateExportFailedStatus,
                    "Export failed: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                LogLocalized(
                    UiTextKey.BulkOperationsTemplateExportFailedLog,
                    "Bulk CSV template export failed: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }
    }

    public class BulkEntryPreview
    {
        public string Customer { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string M365Group { get; set; } = string.Empty;
        public string CqDisplayName { get; set; } = string.Empty;
        public string AaDisplayName { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public PhoneManagerVariables Variables { get; set; } = new();
    }
}
