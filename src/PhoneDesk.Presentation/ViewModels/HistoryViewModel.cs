using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Audit;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.ViewModels
{
    /// <summary>
    /// Backs the History page: reads the persistent audit trail (all tenants) and presents it with
    /// client-side filters for date range, object, and outcome, plus a toggle to include the
    /// lower-level read/query entries. Read-only page — it never mutates the tenant.
    /// </summary>
    public partial class HistoryViewModel : ViewModelBase
    {
        private readonly IAuditLog _auditLogService;
        private IReadOnlyList<AuditRecord> _all = Array.Empty<AuditRecord>();

        /// <summary>The filtered, display-ready records (newest first).</summary>
        public ObservableCollection<AuditRecord> Records { get; } = new();

        [ObservableProperty]
        private DateTimeOffset? _fromDate;

        [ObservableProperty]
        private DateTimeOffset? _toDate;

        [ObservableProperty]
        private string _objectFilter = string.Empty;

        /// <summary>0 = All, 1 = Success, 2 = Failure, 3 = Cancelled.</summary>
        [ObservableProperty]
        private int _selectedOutcomeIndex;

        /// <summary>When false (default) the lower-level read/query entries are excluded from the view.</summary>
        [ObservableProperty]
        private bool _showReadOperations;

        [ObservableProperty]
        private string _auditLogDirectory = string.Empty;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private int _visibleCount;

        public bool HasRecords => VisibleCount > 0;

        public HistoryViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            IAuditLog auditLog,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, auditLog: auditLog, translationService: translationService)
        {
            _auditLogService = auditLog;
            AuditLogDirectory = auditLog.LogDirectoryPath;
            _loggingService.Log("History page loaded", LogLevel.Info);
            Load();
        }

        [RelayCommand]
        private void Load()
        {
            try
            {
                _all = _auditLogService.Read();
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Failed to read audit log: {ex.Message}", LogLevel.Warning);
                _all = Array.Empty<AuditRecord>();
            }

            TotalCount = _all.Count;
            ApplyFilters();
        }

        partial void OnFromDateChanged(DateTimeOffset? value) => ApplyFilters();
        partial void OnToDateChanged(DateTimeOffset? value) => ApplyFilters();
        partial void OnObjectFilterChanged(string value) => ApplyFilters();
        partial void OnSelectedOutcomeIndexChanged(int value) => ApplyFilters();
        partial void OnShowReadOperationsChanged(bool value) => ApplyFilters();
        partial void OnVisibleCountChanged(int value) => OnPropertyChanged(nameof(HasRecords));

        private void ApplyFilters()
        {
            IEnumerable<AuditRecord> query = _all;

            if (!ShowReadOperations)
            {
                query = query.Where(r => r.Kind != AuditKind.Read);
            }

            var outcome = OutcomeForIndex(SelectedOutcomeIndex);
            if (outcome is { } wanted)
            {
                query = query.Where(r => r.Outcome == wanted);
            }

            var text = ObjectFilter?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                query = query.Where(r =>
                    (r.Target?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    r.Operation.Contains(text, StringComparison.OrdinalIgnoreCase));
            }

            if (FromDate is { } from)
            {
                var fromDay = from.UtcDateTime.Date;
                query = query.Where(r => r.TimestampUtc.UtcDateTime.Date >= fromDay);
            }

            if (ToDate is { } to)
            {
                var toDay = to.UtcDateTime.Date;
                query = query.Where(r => r.TimestampUtc.UtcDateTime.Date <= toDay);
            }

            Records.Clear();
            foreach (var record in query)
            {
                Records.Add(record);
            }

            VisibleCount = Records.Count;
        }

        private static AuditOutcome? OutcomeForIndex(int index) => index switch
        {
            1 => AuditOutcome.Success,
            2 => AuditOutcome.Failure,
            3 => AuditOutcome.Cancelled,
            _ => null
        };

        [RelayCommand]
        private void ClearFilters()
        {
            FromDate = null;
            ToDate = null;
            ObjectFilter = string.Empty;
            SelectedOutcomeIndex = 0;
            ShowReadOperations = false;
        }

        [RelayCommand]
        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(AuditLogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AuditLogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Could not open audit log folder: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
