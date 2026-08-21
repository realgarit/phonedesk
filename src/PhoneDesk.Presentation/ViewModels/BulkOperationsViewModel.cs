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
            _loggingService.Log("Bulk Operations page loaded", LogLevel.Info);
        }

        [RelayCommand]
        private void GenerateTemplate()
        {
            CsvContent = _bulkBuilder.GenerateCsvTemplate();
            StatusMessage = "CSV template generated. Edit the data and click 'Parse CSV'.";
            _loggingService.Log("Bulk CSV template generated", LogLevel.Info);
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
                    StatusMessage = "Cannot open file picker.";
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
                    StatusMessage = $"CSV file loaded: {files[0].Name}";
                    _loggingService.Log($"Bulk CSV imported from file: {files[0].Name}", LogLevel.Info);
                    ParseCsvContent();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to import: {ex.Message}";
                _loggingService.Log($"Bulk CSV import failed: {ex.Message}", LogLevel.Error);
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
                    StatusMessage = "No CSV content to parse.";
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
                    StatusMessage = $"Parsed {ParsedEntries.Count} entries. Click 'Preview Script' to review, then 'Execute All'.";
                    _loggingService.Log($"Bulk CSV parsed: {ParsedEntries.Count} entries", LogLevel.Info);
                }
                else
                {
                    StatusMessage = "No valid entries found in CSV. Check the format.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Parse error: {ex.Message}";
                _loggingService.Log($"Bulk CSV parse error: {ex.Message}", LogLevel.Error);
            }
        }

        [RelayCommand]
        private void PreviewScript()
        {
            if (ParsedEntries.Count == 0)
            {
                StatusMessage = "No entries to preview. Parse CSV first.";
                return;
            }

            var entries = new System.Collections.Generic.List<PhoneManagerVariables>();
            foreach (var entry in ParsedEntries)
            {
                entries.Add(entry.Variables);
            }

            ScriptPreview = _bulkBuilder.GenerateBulkScript(entries);
            StatusMessage = $"Script preview generated for {entries.Count} entries.";
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
                StatusMessage = "Plan preview is unavailable.";
                return;
            }

            if (ParsedEntries.Count == 0)
            {
                StatusMessage = "No entries to plan. Parse CSV first.";
                return;
            }

            Plan = _planBuilder.BuildBulkPlan(CollectParsedVariables());
            StatusMessage = Plan.InvalidEntryCount > 0
                ? $"Plan generated: {Plan.ValidEntryCount} valid, {Plan.InvalidEntryCount} invalid of {Plan.EntryCount} rows. Fix issues or enable 'Skip invalid rows'."
                : $"Plan generated: {Plan.EntryCount} rows, {Plan.TotalObjectCount} objects would be created/changed.";
            _loggingService.Log($"Bulk dry-run plan generated: {Plan.EntryCount} rows ({Plan.InvalidEntryCount} invalid)", LogLevel.Info);
        }

        [RelayCommand]
        private Task ExportPlanJsonAsync() => ExportPlanAsync(asJson: true);

        [RelayCommand]
        private Task ExportPlanCsvAsync() => ExportPlanAsync(asJson: false);

        private async Task ExportPlanAsync(bool asJson)
        {
            if (_planBuilder == null || _planExporter == null)
            {
                StatusMessage = "Plan export is unavailable.";
                return;
            }

            if (ParsedEntries.Count == 0)
            {
                StatusMessage = "No entries to export. Parse CSV first.";
                return;
            }

            Plan ??= _planBuilder.BuildBulkPlan(CollectParsedVariables());

            var content = asJson ? _planExporter.ToJson(Plan) : _planExporter.ToCsv(Plan);
            var extension = asJson ? "json" : "csv";
            var saved = await DryRunPlanExportHelper.SavePlanAsync(content, $"bulk-dry-run-plan.{extension}", extension);
            if (saved != null)
            {
                StatusMessage = $"Plan exported to {saved}";
                _loggingService.Log($"Bulk dry-run plan exported to: {saved}", LogLevel.Info);
            }
            else
            {
                StatusMessage = "Plan export cancelled.";
            }
        }

        [RelayCommand]
        private async Task ExecuteAllAsync()
        {
            if (ParsedEntries.Count == 0)
            {
                StatusMessage = "No entries to execute. Parse CSV first.";
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
                        StatusMessage = $"{Plan.InvalidEntryCount} of {Plan.EntryCount} row(s) are invalid. Fix them, or enable 'Skip invalid rows' to run only the valid rows.";
                        _loggingService.Log($"Bulk execution blocked: {Plan.InvalidEntryCount} invalid rows and skip-invalid is off", LogLevel.Warning);
                        return;
                    }

                    var validEntries = Plan.ValidEntries.Select(e => entries[e.RowNumber - 1]).ToList();
                    if (validEntries.Count == 0)
                    {
                        StatusMessage = "All rows are invalid. Nothing to execute.";
                        return;
                    }

                    _loggingService.Log($"Bulk execution skipping {Plan.InvalidEntryCount} invalid row(s); running {validEntries.Count} valid row(s)", LogLevel.Warning);
                    entries = validEntries;
                }
            }

            var bulkScript = _bulkBuilder.GenerateBulkScript(entries);

            // Show confirmation with preview
            if (_dialogService != null)
            {
                var confirmed = await _dialogService.ShowConfirmationWithPreviewAsync(
                    "Execute Bulk Operations",
                    $"This will execute {entries.Count} complete phone system setups. This action creates M365 Groups, Resource Accounts, Call Queues, and Auto Attendants for all entries.",
                    bulkScript);

                if (!confirmed)
                {
                    StatusMessage = "Bulk execution cancelled.";
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

                StatusMessage = $"Executing bulk operations for {entries.Count} entries...";
                _loggingService.Log($"Bulk execution started: {entries.Count} entries", LogLevel.Info);

                // Execute the entire script as one batch
                var result = await ExecutePowerShellCommandAsync(bulkScript, "BulkOperations");

                if (!string.IsNullOrEmpty(result.Value))
                {
                    log.AppendLine(result.Value);

                    if (result.HasErrorMarker)
                    {
                        StatusMessage = "Bulk execution completed with errors. Check the log below.";
                        _loggingService.Log("Bulk execution completed with errors", LogLevel.Warning);
                    }
                    else
                    {
                        StatusMessage = $"Bulk execution completed successfully for {entries.Count} entries!";
                        _loggingService.Log($"Bulk execution completed successfully: {entries.Count} entries", LogLevel.Info);
                    }
                }
                else
                {
                    log.AppendLine("Script executed (no output returned).");
                    StatusMessage = $"Bulk execution completed for {entries.Count} entries.";
                }

                ProcessedCount = TotalCount;
                ExecutionLog = log.ToString();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Bulk execution failed: {ex.Message}";
                ExecutionLog += $"\nFATAL ERROR: {ex.Message}";
                _loggingService.Log($"Bulk execution exception: {ex}", LogLevel.Error);
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
                    StatusMessage = "Cannot open file picker.";
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
                    StatusMessage = $"Template exported to {file.Name}";
                    _loggingService.Log($"Bulk CSV template exported to: {file.Name}", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
                _loggingService.Log($"Bulk CSV template export failed: {ex.Message}", LogLevel.Error);
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
