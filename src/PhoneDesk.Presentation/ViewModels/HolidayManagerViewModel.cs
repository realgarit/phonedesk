using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Localization;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    public enum HolidayOperationKind
    {
        None,
        EditDates,
        ReplaceGreeting,
        Attach,
        Retire,
        CompareList
    }

    /// <summary>A holiday schedule of the tenant, as a row.</summary>
    public sealed class HolidayScheduleRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Dates { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string UsedBy { get; init; } = string.Empty;
        public bool IsExpired { get; init; }
        public override string ToString() => Name;
    }

    public sealed class HolidayAttendantRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public override string ToString() => Name;
    }

    /// <summary>A holiday of one auto attendant: the schedule and what callers get.</summary>
    public sealed class HolidayLinkRow
    {
        public string ScheduleId { get; init; } = string.Empty;
        public string ScheduleName { get; init; } = string.Empty;
        public string Dates { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string CallHandling { get; init; } = string.Empty;
        public bool IsExpired { get; init; }
    }

    /// <summary>An auto attendant that can get a schedule in the attach dialog.</summary>
    public partial class HolidayAttendantChoice : ObservableObject
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        [ObservableProperty]
        private bool _isSelected;
    }

    /// <summary>
    /// Holiday schedules that exist in the tenant: what callers get on them per auto attendant, and
    /// the changes to them (other dates, another greeting, a schedule for more auto attendants,
    /// retiring). Checks, scripts and the read-back come from <see cref="HolidayScheduleOperations"/>
    /// and <see cref="HolidayCallHandling"/>, the same code the MCP server uses.
    /// </summary>
    public partial class HolidayManagerViewModel : ViewModelBase
    {
        private readonly TimeProvider _time;
        private HolidayCallHandlingState? _state;

        /// <summary>What the tenant held when this page first read it: the "before" of the change table.</summary>
        private HolidayCallHandlingState? _baseline;
        private string? _approvedState;
        private string? _pendingScript;
        private Func<HolidayCallHandlingState, string>? _verify;
        private readonly IHolidayBackupStore? _backupStore;
        private IReadOnlyList<string> _scopeAttendants = Array.Empty<string>();
        private IReadOnlyList<string> _scopeSchedules = Array.Empty<string>();

        public ObservableCollection<HolidayScheduleRow> Schedules { get; } = new();
        public ObservableCollection<HolidayAttendantRow> AutoAttendants { get; } = new();
        public ObservableCollection<HolidayLinkRow> Holidays { get; } = new();
        public ObservableCollection<HolidayScheduleRow> TargetChoices { get; } = new();
        public ObservableCollection<HolidayAttendantChoice> AttendantChoices { get; } = new();

        [ObservableProperty]
        private HolidayScheduleRow? _selectedSchedule;

        [ObservableProperty]
        private HolidayAttendantRow? _selectedAutoAttendant;

        [ObservableProperty]
        private HolidayLinkRow? _selectedHoliday;

        [ObservableProperty]
        private HolidayScheduleRow? _selectedTarget;

        [ObservableProperty]
        private bool _isLoaded;

        [ObservableProperty]
        private string _attendantSummary = string.Empty;

        [ObservableProperty]
        private HolidayOperationKind _operation = HolidayOperationKind.None;

        [ObservableProperty]
        private string _operationTitle = string.Empty;

        [ObservableProperty]
        private string _operationText = string.Empty;

        [ObservableProperty]
        private string _operationSecondText = string.Empty;

        [ObservableProperty]
        private string _operationPreview = string.Empty;

        [ObservableProperty]
        private bool _acceptOverlaps;

        [ObservableProperty]
        private bool _removeMissingUsers;

        [ObservableProperty]
        private bool _replaceExpiredAtLimit = true;

        [ObservableProperty]
        private bool _allowNotExpired;

        [ObservableProperty]
        private bool _canApplyOperation;

        public HolidayManagerViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null,
            TimeProvider? timeProvider = null,
            IHolidayBackupStore? backupStore = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _time = timeProvider ?? TimeProvider.System;
            _backupStore = backupStore;
        }

        public bool ShowEmptyState => !IsLoaded;
        public bool ShowOperationDialog => Operation != HolidayOperationKind.None;
        public bool IsEditDates => Operation == HolidayOperationKind.EditDates;
        public bool IsReplaceGreeting => Operation == HolidayOperationKind.ReplaceGreeting;
        public bool IsAttach => Operation == HolidayOperationKind.Attach;
        public bool IsRetire => Operation == HolidayOperationKind.Retire;
        public bool IsCompareList => Operation == HolidayOperationKind.CompareList;

        /// <summary>A comparison changes nothing, so it has nothing to apply.</summary>
        public bool ShowApply => !IsCompareList;
        public bool ShowOverlapOption => IsEditDates || IsAttach;
        public bool HasOperationPreview => !string.IsNullOrEmpty(OperationPreview);

        partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

        partial void OnOperationChanged(HolidayOperationKind value)
        {
            OnPropertyChanged(nameof(ShowOperationDialog));
            OnPropertyChanged(nameof(IsEditDates));
            OnPropertyChanged(nameof(IsReplaceGreeting));
            OnPropertyChanged(nameof(IsAttach));
            OnPropertyChanged(nameof(IsRetire));
            OnPropertyChanged(nameof(IsCompareList));
            OnPropertyChanged(nameof(ShowApply));
            OnPropertyChanged(nameof(ShowOverlapOption));
        }

        partial void OnOperationPreviewChanged(string value) => OnPropertyChanged(nameof(HasOperationPreview));

        partial void OnSelectedAutoAttendantChanged(HolidayAttendantRow? value) => FillHolidays();

        // An input that changes makes the last check stale: Apply needs a new check.
        partial void OnOperationTextChanged(string value) => InvalidateCheck();
        partial void OnOperationSecondTextChanged(string value) => InvalidateCheck();
        partial void OnAcceptOverlapsChanged(bool value) => InvalidateCheck();
        partial void OnRemoveMissingUsersChanged(bool value) => InvalidateCheck();
        partial void OnReplaceExpiredAtLimitChanged(bool value) => InvalidateCheck();
        partial void OnAllowNotExpiredChanged(bool value) => InvalidateCheck();
        partial void OnSelectedTargetChanged(HolidayScheduleRow? value) => InvalidateCheck();

        private void InvalidateCheck()
        {
            CanApplyOperation = false;
            _approvedState = null;
            _pendingScript = null;
            _verify = null;
            _scopeAttendants = Array.Empty<string>();
            _scopeSchedules = Array.Empty<string>();
        }

        [RelayCommand]
        private async Task LoadAsync()
        {
            await RunBusyAsync(async () =>
            {
                var state = await ReadStateAsync();
                if (state is null)
                {
                    return;
                }

                ShowState(state);
                StatusMessage = GetText(
                    UiTextKey.HolidayManagerLoadedStatus,
                    "{schedules} holiday schedules and {attendants} auto attendants were read.",
                    new Dictionary<string, object?> { ["schedules"] = Schedules.Count, ["attendants"] = AutoAttendants.Count });
            }, nameof(LoadAsync));
        }

        [RelayCommand]
        private void OpenEditDates()
        {
            var schedule = _state?.FindSchedule(SelectedSchedule?.Id);
            if (schedule is null)
            {
                StatusMessage = GetText(UiTextKey.HolidayManagerSelectScheduleStatus, "Select a holiday schedule first.");
                return;
            }

            Open(HolidayOperationKind.EditDates, GetText(UiTextKey.HolidayManagerEditDatesAction, "Change dates") + ": " + schedule.Name);
            OperationText = string.Join(Environment.NewLine, schedule.Windows.OrderBy(window => window.Start).Select(HolidayScheduleOperations.Format));
            InvalidateCheck();
        }

        [RelayCommand]
        private void OpenReplaceGreeting()
        {
            var attendant = _state?.FindAutoAttendant(SelectedAutoAttendant?.Id);
            var link = attendant?.HolidayLinkFor(SelectedHoliday?.ScheduleId);
            if (attendant is null || link is null)
            {
                StatusMessage = GetText(UiTextKey.HolidayManagerSelectHolidayStatus, "Select an auto attendant and one of its holidays first.");
                return;
            }

            var greeting = attendant.FindCallFlow(link.CallFlowId)?.Greetings.FirstOrDefault(prompt => !string.IsNullOrEmpty(prompt.Text) || prompt.AudioFile is not null);
            Open(HolidayOperationKind.ReplaceGreeting, GetText(UiTextKey.HolidayManagerReplaceGreetingAction, "Replace greeting") + ": " + SelectedHoliday!.ScheduleName);
            OperationText = greeting?.AudioFile is null ? greeting?.Text ?? string.Empty : string.Empty;
            OperationSecondText = greeting?.AudioFile?.Id ?? string.Empty;
            InvalidateCheck();
        }

        [RelayCommand]
        private void OpenAttach()
        {
            var source = _state?.FindSchedule(SelectedHoliday?.ScheduleId);
            if (_state is null || source is null || SelectedAutoAttendant is null)
            {
                StatusMessage = GetText(UiTextKey.HolidayManagerSelectHolidayStatus, "Select an auto attendant and one of its holidays first.");
                return;
            }

            Open(HolidayOperationKind.Attach, GetText(UiTextKey.HolidayManagerAttachAction, "Use for another schedule") + ": " + source.Name);
            TargetChoices.Clear();
            foreach (var row in Schedules.Where(row => !row.IsExpired && !HolidayCallHandling.SameId(row.Id, source.Id)))
            {
                TargetChoices.Add(row);
            }

            AttendantChoices.Clear();
            foreach (var attendant in _state.AutoAttendants
                         .Where(attendant => attendant.HolidayLinkFor(source.Id) is not null)
                         .OrderBy(attendant => attendant.Name, StringComparer.OrdinalIgnoreCase))
            {
                var choice = new HolidayAttendantChoice
                {
                    Id = attendant.Id,
                    Name = attendant.Name,
                    IsSelected = HolidayCallHandling.SameId(attendant.Id, SelectedAutoAttendant.Id)
                };
                choice.PropertyChanged += (_, _) => InvalidateCheck();
                AttendantChoices.Add(choice);
            }

            SelectedTarget = TargetChoices.FirstOrDefault(row => HolidayCallHandling.SameId(row.Id, SelectedSchedule?.Id)) ?? TargetChoices.FirstOrDefault();
            InvalidateCheck();
        }

        [RelayCommand]
        private void OpenRetire()
        {
            if (_state?.FindSchedule(SelectedSchedule?.Id) is not { } schedule)
            {
                StatusMessage = GetText(UiTextKey.HolidayManagerSelectScheduleStatus, "Select a holiday schedule first.");
                return;
            }

            Open(HolidayOperationKind.Retire, GetText(UiTextKey.HolidayManagerRetireAction, "Retire schedule") + ": " + schedule.Name);
            InvalidateCheck();
        }

        [RelayCommand]
        private void OpenCompareList()
        {
            if (_state is null)
            {
                StatusMessage = GetText(UiTextKey.HolidayManagerReadFirstStatus, "Read the tenant first.");
                return;
            }

            Open(HolidayOperationKind.CompareList, GetText(UiTextKey.HolidayManagerCompareAction, "Compare a customer list"));
            OperationSecondText = _time.GetLocalNow().Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
            InvalidateCheck();
        }

        [RelayCommand]
        private void CloseOperation()
        {
            Operation = HolidayOperationKind.None;
            OperationPreview = string.Empty;
            InvalidateCheck();
        }

        /// <summary>Reads the tenant and shows what the change would do. Nothing is sent.</summary>
        [RelayCommand]
        private async Task CheckOperationAsync()
        {
            await RunBusyAsync(async () =>
            {
                var state = await ReadStateAsync();
                if (state is null)
                {
                    return;
                }

                Prepare(state);
            }, nameof(CheckOperationAsync));
        }

        /// <summary>
        /// Runs the change that was checked. The tenant is read again first: when it no longer is
        /// what the check showed, nothing runs and the new check is shown.
        /// </summary>
        [RelayCommand]
        private async Task ApplyOperationAsync()
        {
            if (!CanApplyOperation || _approvedState is null)
            {
                return;
            }

            await RunBusyAsync(async () =>
            {
                var approved = _approvedState;
                var state = await ReadStateAsync();
                if (state is null)
                {
                    return;
                }

                Prepare(state);
                if (!CanApplyOperation || _pendingScript is null || !string.Equals(approved, _approvedState, StringComparison.Ordinal))
                {
                    StatusMessage = GetText(
                        UiTextKey.HolidayManagerTenantChangedStatus,
                        "The tenant changed after the check. Nothing was changed. Read the new check, then apply again.");
                    return;
                }

                var script = _pendingScript;
                var verify = _verify;
                var title = OperationTitle;

                // What the change touches is written to a file first. Without that file nothing runs.
                string? backupFile = null;
                if (_backupStore is not null)
                {
                    var backup = HolidayBackup.Create(state, _scopeAttendants, _scopeSchedules, _time.GetUtcNow());
                    try
                    {
                        if (!backup.IsComplete)
                        {
                            throw new System.IO.InvalidDataException(backup.MissingText);
                        }

                        backupFile = _backupStore.Write(backup.Document);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
                    {
                        StatusMessage = GetText(
                            UiTextKey.HolidayManagerBackupFailedStatus,
                            "Nothing was changed: the backup of the objects this change touches could not be written. {details}",
                            new Dictionary<string, object?> { ["details"] = ex.Message });
                        return;
                    }
                }

                Operation = HolidayOperationKind.None;
                var result = await PreviewAndExecuteAsync(script, title);
                if (result is null)
                {
                    StatusMessage = GetText(UiTextKey.RuntimeOperationCancelledByUser, "Operation cancelled by user");
                    return;
                }

                // The tenant decides what happened, not the last line of the script.
                var after = await ReadStateAsync();
                if (after is null)
                {
                    return;
                }

                ShowState(after);
                var problem = verify?.Invoke(after);
                var outcome = string.IsNullOrEmpty(problem)
                    ? GetText(UiTextKey.HolidayManagerVerifiedStatus, "Saved. The tenant was read again and shows the change.")
                    : GetText(
                        UiTextKey.HolidayManagerNotVerifiedStatus,
                        "The tenant does not show the whole change: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Succeeded ? problem : problem + " " + result.ErrorMessage });
                StatusMessage = backupFile is null
                    ? outcome
                    : outcome + " " + GetText(
                        UiTextKey.HolidayManagerBackupNote,
                        "Backup of the state before the change: {file}",
                        new Dictionary<string, object?> { ["file"] = backupFile });
            }, nameof(ApplyOperationAsync));
        }

        /// <summary>Shows a state without a tenant, for the headless renders of the page.</summary>
        public void ShowStateForDisplay(string readOutput)
        {
            _state = HolidayCallHandling.ParseState(readOutput);
            _baseline ??= _state;
            ShowState(_state);
        }

        /// <summary>Runs the check of the open dialog on the shown state, for the headless renders.</summary>
        public void CheckForDisplay()
        {
            if (_state is not null)
            {
                Prepare(_state);
            }
        }

        private void Open(HolidayOperationKind kind, string title)
        {
            OperationText = string.Empty;
            OperationSecondText = string.Empty;
            OperationPreview = string.Empty;
            AcceptOverlaps = false;
            RemoveMissingUsers = false;
            ReplaceExpiredAtLimit = true;
            AllowNotExpired = false;
            OperationTitle = title;
            Operation = kind;
        }

        private async Task<HolidayCallHandlingState?> ReadStateAsync()
        {
            var result = await ExecutePowerShellCommandAsync(
                HolidayCallHandling.GetReadCommand(null, includeAutoAttendants: true, checkAuthorizedUsers: true),
                null,
                "ReadHolidayCallHandling",
                allowThrottleRetry: true);
            try
            {
                _state = HolidayCallHandling.ParseState(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
                _baseline ??= _state;
                return _state;
            }
            catch (System.IO.InvalidDataException ex)
            {
                StatusMessage = GetText(
                    UiTextKey.HolidayManagerLoadFailedStatus,
                    "The holiday schedules could not be read: {details}",
                    new Dictionary<string, object?> { ["details"] = string.IsNullOrWhiteSpace(result.ErrorMessage) ? ex.Message : result.ErrorMessage });
                return null;
            }
        }

        private DateTime Now => _time.GetLocalNow().DateTime;

        private void ShowState(HolidayCallHandlingState state)
        {
            var selectedSchedule = SelectedSchedule?.Id;
            var selectedAttendant = SelectedAutoAttendant?.Id;
            var names = state.AutoAttendants.ToDictionary(attendant => attendant.Id, attendant => attendant.Name, StringComparer.OrdinalIgnoreCase);
            Schedules.Clear();
            foreach (var schedule in state.Schedules.Where(schedule => schedule.IsFixed)
                         .OrderBy(schedule => schedule.Windows.Count == 0 ? DateTime.MaxValue : schedule.Windows.Min(window => window.Start))
                         .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase))
            {
                Schedules.Add(new HolidayScheduleRow
                {
                    Id = schedule.Id,
                    Name = schedule.Name,
                    Dates = DatesOf(schedule),
                    IsExpired = schedule.IsExpired(Now),
                    Status = StatusOf(schedule),
                    UsedBy = string.Join(", ", schedule.UsedBy.Select(id => names.TryGetValue(id, out var name) ? name : id))
                });
            }

            AutoAttendants.Clear();
            foreach (var attendant in state.AutoAttendants.OrderBy(attendant => attendant.Name, StringComparer.OrdinalIgnoreCase))
            {
                AutoAttendants.Add(new HolidayAttendantRow { Id = attendant.Id, Name = attendant.Name });
            }

            IsLoaded = true;
            SelectedSchedule = Schedules.FirstOrDefault(row => HolidayCallHandling.SameId(row.Id, selectedSchedule));
            SelectedAutoAttendant = AutoAttendants.FirstOrDefault(row => HolidayCallHandling.SameId(row.Id, selectedAttendant)) ?? AutoAttendants.FirstOrDefault();
            FillHolidays();
        }

        private void FillHolidays()
        {
            Holidays.Clear();
            var attendant = _state?.FindAutoAttendant(SelectedAutoAttendant?.Id);
            if (_state is null || attendant is null)
            {
                AttendantSummary = string.Empty;
                return;
            }

            foreach (var item in attendant.HolidayLinks
                         .Select(link => (Link: link, Schedule: _state.FindSchedule(link.ScheduleId)))
                         .OrderBy(item => item.Schedule?.Windows.Count > 0 ? item.Schedule.Windows.Min(window => window.Start) : DateTime.MaxValue))
            {
                Holidays.Add(new HolidayLinkRow
                {
                    ScheduleId = item.Link.ScheduleId,
                    ScheduleName = item.Schedule?.Name ?? item.Link.ScheduleId,
                    Dates = item.Schedule is null ? string.Empty : DatesOf(item.Schedule),
                    IsExpired = item.Schedule?.IsExpired(Now) == true,
                    Status = item.Schedule is null ? string.Empty : StatusOf(item.Schedule),
                    CallHandling = HolidayCallHandling.Describe(attendant.FindCallFlow(item.Link.CallFlowId))
                });
            }

            AttendantSummary = GetText(
                UiTextKey.HolidayManagerAttendantSummary,
                "{flows} of {limit} call flows used. {overlaps} overlapping holidays. {missing} authorized users that no longer exist.",
                new Dictionary<string, object?>
                {
                    ["flows"] = attendant.CallFlows.Count,
                    ["limit"] = HolidayCallHandling.AdditionalCallFlowLimit,
                    ["overlaps"] = HolidayCallHandling.FindOverlaps(_state, attendant).Count,
                    ["missing"] = _state.MissingAuthorizedUsers.Count(user => HolidayCallHandling.SameId(user.AutoAttendantId, attendant.Id))
                });
        }

        private static string DatesOf(ScheduleInfo schedule)
            => string.Join("; ", schedule.Windows.OrderBy(window => window.Start).Select(HolidayScheduleOperations.Format));

        private string StatusOf(ScheduleInfo schedule)
            => schedule.IsExpired(Now)
                ? GetText(UiTextKey.HolidayManagerStatusExpired, "Expired")
                : GetText(UiTextKey.HolidayManagerStatusCurrent, "Current");

        /// <summary>Checks the open operation against a state and fills the preview, the script and the approval text.</summary>
        private void Prepare(HolidayCallHandlingState state)
        {
            InvalidateCheck();
            var lines = new List<string>();
            IReadOnlyList<string> blockers = Array.Empty<string>();
            var noChange = false;
            try
            {
                switch (Operation)
                {
                    case HolidayOperationKind.EditDates:
                        (blockers, noChange) = PrepareDates(state, lines);
                        break;
                    case HolidayOperationKind.ReplaceGreeting:
                        (blockers, noChange) = PrepareGreeting(state, lines);
                        break;
                    case HolidayOperationKind.Attach:
                        (blockers, noChange) = PrepareAttach(state, lines);
                        break;
                    case HolidayOperationKind.Retire:
                        blockers = PrepareRetire(state, lines);
                        break;
                    case HolidayOperationKind.CompareList:
                        PrepareComparison(state, lines);
                        OperationPreview = string.Join(Environment.NewLine, lines);
                        return;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                blockers = new[] { ex.Message };
            }

            if (blockers.Count > 0)
            {
                InvalidateCheck();
                lines.Add(GetText(
                    UiTextKey.HolidayManagerPreviewBlocked,
                    "Cannot be saved: {details}",
                    new Dictionary<string, object?> { ["details"] = string.Join(" ", blockers) }));
            }
            else if (noChange)
            {
                InvalidateCheck();
                lines.Add(GetText(UiTextKey.HolidayManagerPreviewNoChange, "The tenant has this already. There is nothing to save."));
            }
            else
            {
                CanApplyOperation = _pendingScript is not null;
                lines.Add(GetText(UiTextKey.HolidayManagerPreviewReady, "Ready. Apply shows the script before it runs."));
            }

            OperationPreview = string.Join(Environment.NewLine, lines);
        }

        private (IReadOnlyList<string> Blockers, bool NoChange) PrepareDates(HolidayCallHandlingState state, List<string> lines)
        {
            var id = SelectedSchedule?.Id ?? string.Empty;
            var wanted = HolidayScheduleOperations.ParseWindows(OperationText);
            var assessment = HolidayScheduleOperations.AssessDates(state, id, wanted, AcceptOverlaps);
            lines.Add(Line(UiTextKey.HolidayManagerPreviewAdded, "Added: {value}", ListOf(assessment.Added.Select(HolidayScheduleOperations.Format))));
            lines.Add(Line(UiTextKey.HolidayManagerPreviewRemoved, "Removed: {value}", ListOf(assessment.Removed.Select(HolidayScheduleOperations.Format))));
            lines.Add(Line(UiTextKey.HolidayManagerPreviewKept, "Kept: {value}", ListOf(assessment.Kept.Select(HolidayScheduleOperations.Format))));
            lines.Add(Line(UiTextKey.HolidayManagerPreviewAffected, "Applies to: {value}", ListOf(assessment.AffectedAutoAttendants.Select(attendant => attendant.Name))));
            if (assessment.CanSave)
            {
                _scopeAttendants = assessment.AffectedAutoAttendants.Select(attendant => attendant.Id).ToList();
                _scopeSchedules = new[] { id };
                _pendingScript = HolidayScheduleOperations.GetSetDatesCommand(id, assessment.Schedule!.Windows, assessment.NewWindows);
                _approvedState = HolidayScheduleOperations.ApprovedState(assessment);
                var expected = assessment.NewWindows;
                _verify = after => after.FindSchedule(id) is { } saved && saved.Windows.Count == expected.Count && expected.All(saved.Windows.Contains)
                    ? string.Empty
                    : DatesOf(after.FindSchedule(id) ?? new ScheduleInfo());
            }

            return (assessment.Blockers.Select(blocker => blocker.Message).ToList(), assessment.Blockers.Count == 0 && assessment.NoChange);
        }

        private (IReadOnlyList<string> Blockers, bool NoChange) PrepareGreeting(HolidayCallHandlingState state, List<string> lines)
        {
            var change = new GreetingChange(
                SelectedAutoAttendant?.Id ?? string.Empty,
                SelectedHoliday?.ScheduleId ?? string.Empty,
                string.IsNullOrWhiteSpace(OperationText) ? null : OperationText.Trim(),
                string.IsNullOrWhiteSpace(OperationSecondText) ? null : OperationSecondText.Trim());
            var assessment = HolidayScheduleOperations.AssessGreeting(state, change);
            lines.Add(Line(UiTextKey.HolidayManagerPreviewOldHandling, "Now: {value}", HolidayCallHandling.Describe(assessment.CallFlow)));
            lines.Add(Line(
                UiTextKey.HolidayManagerPreviewNewGreeting,
                "New greeting: {value}",
                change.AudioFileId ?? change.Text ?? string.Empty));
            if (assessment.OtherSchedulesOnTheCallFlow.Count > 0)
            {
                lines.Add(Line(
                    UiTextKey.HolidayManagerPreviewAlsoChanges,
                    "The same call flow serves: {value}",
                    ListOf(assessment.OtherSchedulesOnTheCallFlow.Select(schedule => schedule.Name))));
            }

            if (assessment.CanSave)
            {
                _scopeAttendants = new[] { change.AutoAttendantId };
                _scopeSchedules = new[] { change.ScheduleId };
                _pendingScript = HolidayScheduleOperations.GetSetGreetingCommand(change);
                _approvedState = HolidayScheduleOperations.ApprovedState(assessment);
                _verify = after =>
                {
                    var attendant = after.FindAutoAttendant(change.AutoAttendantId);
                    var flow = attendant?.FindCallFlow(attendant.HolidayLinkFor(change.ScheduleId)?.CallFlowId);
                    return flow is not null && HolidayScheduleOperations.GreetingIs(flow, change) ? string.Empty : HolidayCallHandling.Describe(flow);
                };
            }

            return (assessment.Blockers.Select(blocker => blocker.Message).ToList(), assessment.NoChange);
        }

        private (IReadOnlyList<string> Blockers, bool NoChange) PrepareAttach(HolidayCallHandlingState state, List<string> lines)
        {
            var source = SelectedHoliday?.ScheduleId ?? string.Empty;
            var target = SelectedTarget?.Id ?? string.Empty;
            var chosen = AttendantChoices.Where(choice => choice.IsSelected).ToList();
            if (string.IsNullOrEmpty(target) || chosen.Count == 0)
            {
                return (new[] { GetText(UiTextKey.HolidayManagerAttachNeedsTargetStatus, "Select the schedule to attach and one auto attendant at least.") }, false);
            }

            var now = Now;
            var items = chosen.Select(choice => new RolloutItem(choice.Id, source, ReplaceFor(state, choice.Id, source, target, now))).ToList();
            var planned = HolidayScheduleOperations.AssessRollout(state, target, items, AcceptOverlaps, RemoveMissingUsers, _ => now);
            foreach (var result in planned)
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerPreviewRolloutItem,
                    "{name}: {outcome}. {details}",
                    new Dictionary<string, object?>
                    {
                        ["name"] = result.Assessment.AutoAttendant?.Name ?? result.Item.AutoAttendantId,
                        ["outcome"] = OutcomeText(result.Outcome),
                        ["details"] = result.Outcome == RolloutOutcome.Blocked
                            ? string.Join(" ", result.Assessment.Blockers.Select(blocker => blocker.Message))
                            : HolidayCallHandling.Describe(result.Assessment.SourceCallFlow) +
                              (result.Assessment.ReplacedSchedule is null ? string.Empty : " (-" + result.Assessment.ReplacedSchedule.Name + ")")
                    }));
            }

            var toSave = planned.Count(result => result.Outcome == RolloutOutcome.Planned);
            if (toSave > 0)
            {
                (_scopeAttendants, _scopeSchedules) = HolidayScheduleOperations.RolloutBackupScope(target, planned);
                _pendingScript = HolidayScheduleOperations.GetRolloutCommand(target, planned, AcceptOverlaps);
                _approvedState = HolidayScheduleOperations.ApprovedState(target, planned);
                _verify = after => string.Join("; ", HolidayScheduleOperations.VerifyRollout(after, target, planned)
                    .Where(result => result.Outcome is RolloutOutcome.Failed or RolloutOutcome.Different)
                    .Select(result => (result.Assessment.AutoAttendant?.Name ?? result.Item.AutoAttendantId) + ": " + OutcomeText(result.Outcome)));
            }

            // Items that are blocked are left out; the change is refused only when nothing is left.
            return (Array.Empty<string>(), toSave == 0);
        }

        private IReadOnlyList<string> PrepareRetire(HolidayCallHandlingState state, List<string> lines)
        {
            var assessment = HolidayScheduleOperations.AssessRetire(state, new[] { SelectedSchedule?.Id ?? string.Empty }, Now, AllowNotExpired);
            if (assessment.NothingLeft)
            {
                return new[] { GetText(UiTextKey.HolidayManagerPreviewNoChange, "The tenant has this already. There is nothing to save.") };
            }

            foreach (var item in assessment.Schedules.Where(item => item.Schedule is not null))
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerPreviewRetireItem,
                    "{name} ({dates}) is deleted. Links taken off: {links}",
                    new Dictionary<string, object?>
                    {
                        ["name"] = item.Schedule!.Name,
                        ["dates"] = DatesOf(item.Schedule),
                        ["links"] = ListOf(item.References.Select(reference => reference.AutoAttendantName))
                    }));
            }

            if (assessment.CanSave)
            {
                _scopeAttendants = assessment.Remaining.SelectMany(item => item.References).Select(reference => reference.AutoAttendantId).ToList();
                _scopeSchedules = assessment.Remaining.Select(item => item.ScheduleId).ToList();
                _pendingScript = HolidayScheduleOperations.GetRetireCommand(assessment);
                _approvedState = HolidayScheduleOperations.ApprovedState(assessment);
                _verify = after =>
                {
                    var left = HolidayScheduleOperations.RetireLeftovers(after, assessment);
                    return string.Join(", ", left.RemainingSchedules.Concat(left.RemainingLinks.Select(reference => reference.AutoAttendantName)));
                };
            }

            return assessment.Blockers.Select(blocker => blocker.Message).ToList();
        }

        /// <summary>
        /// Compares the customer's list with every auto attendant, and lists what changed since the page
        /// first read the tenant: only what the tenant shows now counts as done.
        /// </summary>
        private void PrepareComparison(HolidayCallHandlingState state, List<string> lines)
        {
            if (!int.TryParse(OperationSecondText.Trim(), out var year) || year is < 2000 or > 2100)
            {
                lines.Add(GetText(UiTextKey.HolidayManagerCompareYearInvalid, "Enter the year of the list, for example 2027."));
                return;
            }

            var list = HolidayListComparer.Parse(OperationText, year);
            foreach (var line in list.Unreadable)
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerCompareUnreadable,
                    "Line {number} was not read: {text}",
                    new Dictionary<string, object?> { ["number"] = line.LineNumber, ["text"] = line.Text }));
            }

            if (list.Years.Count > 1)
            {
                lines.Add(Line(UiTextKey.HolidayManagerCompareSeveralYears, "The list spans several years: {value}", string.Join(", ", list.Years)));
            }

            foreach (var entry in list.Entries.Where(entry => entry.YearWasAssumed))
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerCompareYearAssumed,
                    "The year {year} was assumed for: {text}",
                    new Dictionary<string, object?> { ["year"] = entry.Window.Start.Year, ["text"] = entry.Original }));
            }

            var comparison = HolidayListComparer.Compare(state, list);
            foreach (var row in comparison.Rows)
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerCompareRow,
                    "{line} | {attendant}: {status}{detail}",
                    new Dictionary<string, object?>
                    {
                        ["line"] = row.Entry.Original,
                        ["attendant"] = row.AutoAttendantName,
                        ["status"] = StatusText(row.Status),
                        ["detail"] = row.Schedule is null ? string.Empty : " (" + row.Schedule.Name + ", " + HolidayScheduleOperations.Format(row.ScheduleWindow!) + ")"
                    }));
            }

            foreach (var range in comparison.OnlyInTenant)
            {
                lines.Add(GetText(
                    UiTextKey.HolidayManagerCompareOnlyInTenant,
                    "Only in the tenant: {attendant}: {range} ({schedule})",
                    new Dictionary<string, object?>
                    {
                        ["attendant"] = range.AutoAttendantName,
                        ["range"] = HolidayScheduleOperations.Format(range.Window),
                        ["schedule"] = range.Schedule.Name
                    }));
            }

            if (_baseline is not null && list.Entries.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add(GetText(
                    UiTextKey.HolidayManagerCompareChangesTitle,
                    "Changes since this page first read the tenant (tab-separated, for the customer record):"));
                lines.Add(HolidayListComparer.ToTable(HolidayListComparer.Changes(_baseline, state, list)).TrimEnd('\n'));
            }
        }

        private string StatusText(HolidayListStatus status)
            => status switch
            {
                HolidayListStatus.Match => GetText(UiTextKey.HolidayManagerCompareMatch, "in the tenant"),
                HolidayListStatus.Covered => GetText(UiTextKey.HolidayManagerCompareCovered, "covered by a longer schedule"),
                HolidayListStatus.SameStart => GetText(UiTextKey.HolidayManagerCompareSameStart, "same start, another end"),
                HolidayListStatus.PartialOverlap => GetText(UiTextKey.HolidayManagerComparePartialOverlap, "overlaps partly"),
                HolidayListStatus.Disabled => GetText(UiTextKey.HolidayManagerCompareDisabled, "attached but switched off"),
                _ => GetText(UiTextKey.HolidayManagerCompareMissing, "missing")
            };

        /// <summary>The oldest expired holiday of an auto attendant at the call flow limit, when the option is on.</summary>
        private string? ReplaceFor(HolidayCallHandlingState state, string attendantId, string source, string target, DateTime now)
        {
            var attendant = state.FindAutoAttendant(attendantId);
            if (!ReplaceExpiredAtLimit || attendant is null ||
                attendant.CallFlows.Count < HolidayCallHandling.AdditionalCallFlowLimit ||
                attendant.HolidayLinkFor(target) is not null)
            {
                return null;
            }

            return attendant.HolidayLinks
                .Select(link => state.FindSchedule(link.ScheduleId))
                .Where(schedule => schedule is not null && schedule.IsExpired(now) &&
                                   !HolidayCallHandling.SameId(schedule.Id, source) && !HolidayCallHandling.SameId(schedule.Id, target))
                .OrderBy(schedule => schedule!.LastEnd)
                .FirstOrDefault()?.Id;
        }

        private string OutcomeText(RolloutOutcome outcome)
            => outcome switch
            {
                RolloutOutcome.Verified => GetText(UiTextKey.HolidayManagerOutcomeVerified, "attached already"),
                RolloutOutcome.Different => GetText(UiTextKey.HolidayManagerOutcomeDifferent, "attached with another call handling"),
                RolloutOutcome.Planned => GetText(UiTextKey.HolidayManagerOutcomePlanned, "will be attached"),
                RolloutOutcome.Blocked => GetText(UiTextKey.HolidayManagerOutcomeBlocked, "left out"),
                _ => GetText(UiTextKey.HolidayManagerOutcomeFailed, "not attached")
            };

        private string Line(UiTextKey key, string fallback, string value)
            => GetText(key, fallback, new Dictionary<string, object?> { ["value"] = value });

        private string ListOf(IEnumerable<string> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? GetText(UiTextKey.HolidayManagerNone, "none") : string.Join("; ", list);
        }
    }
}
