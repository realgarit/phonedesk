using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Localization;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    /// <summary>A recorded run, as a row.</summary>
    public sealed class SetupRunRow
    {
        public string Id { get; init; } = string.Empty;
        public string Started { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Objects { get; init; } = string.Empty;
    }

    /// <summary>
    /// The Guided Setup and bulk runs this computer recorded, and the rollback of one of them. A
    /// rollback removes only objects the run created, in reverse order, and only while the tenant
    /// still shows the same object. The rules come from <see cref="RunJournal"/>, the same code the
    /// MCP server uses.
    /// </summary>
    public partial class SetupRunsViewModel : ViewModelBase
    {
        private readonly RunJournalService? _journal;
        private RollbackPlan? _checked;
        private string? _approvedState;
        private string? _checkedRunId;

        public ObservableCollection<SetupRunRow> Runs { get; } = new();

        [ObservableProperty]
        private SetupRunRow? _selectedRun;

        [ObservableProperty]
        private string _details = string.Empty;

        [ObservableProperty]
        private bool _canRollBack;

        public SetupRunsViewModel(
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
            RunJournalService? journal = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _journal = journal;
            Refresh();
        }

        public bool ShowEmptyState => Runs.Count == 0;
        public bool HasRuns => Runs.Count > 0;
        public bool HasDetails => !string.IsNullOrEmpty(Details);

        partial void OnDetailsChanged(string value) => OnPropertyChanged(nameof(HasDetails));

        partial void OnSelectedRunChanged(SetupRunRow? value)
        {
            // Another run makes the last check stale.
            _checked = null;
            _approvedState = null;
            _checkedRunId = null;
            CanRollBack = false;
            ShowRecord();
        }

        /// <summary>Reads the list from this computer. The tenant is not read.</summary>
        [RelayCommand]
        private void Refresh()
        {
            var selected = SelectedRun?.Id;
            Runs.Clear();
            foreach (var record in _journal?.List() ?? Array.Empty<RunRecord>())
            {
                Runs.Add(new SetupRunRow
                {
                    Id = record.Id,
                    Started = record.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    Label = record.Label,
                    Kind = KindText(record.Kind),
                    State = StateText(record.State),
                    Objects = GetText(
                        UiTextKey.SetupRunsObjectCount,
                        "{left} of {total}",
                        new Dictionary<string, object?> { ["left"] = record.Objects.Count(item => !item.Removed), ["total"] = record.Objects.Count })
                });
            }

            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasRuns));
            // The newest run that is not finished is the one an operator comes here for.
            SelectedRun = Runs.FirstOrDefault(row => row.Id == selected)
                ?? Runs.FirstOrDefault(row => _journal?.Find(row.Id) is { IsOpen: true })
                ?? Runs.FirstOrDefault();
        }

        /// <summary>Reads the tenant and shows, per object, whether a rollback removes it. Changes nothing.</summary>
        [RelayCommand]
        private Task CheckRollbackAsync() => RunBusyAsync(async () => await CheckAsync(Selected()), nameof(CheckRollbackAsync));

        /// <summary>Reads the tenant again, and removes the objects only when the check is still the same.</summary>
        [RelayCommand]
        private Task RollBackAsync() => RunBusyAsync(async () =>
        {
            var record = Selected();
            if (record is null || _journal is null || _approvedState is null || _checkedRunId != record.Id)
            {
                return;
            }

            // The check and the result must be about one and the same record: the plan holds its objects.
            var approved = _approvedState;
            var plan = await CheckAsync(record);
            if (plan is null)
            {
                return;
            }

            if (!string.Equals(approved, RunJournal.ApprovedState(plan), StringComparison.Ordinal))
            {
                StatusMessage = GetText(
                    UiTextKey.SetupRunsChangedStatus,
                    "The tenant changed after the check. Nothing was removed. Read the new check, then roll back again.");
                return;
            }

            if (!plan.HasWork)
            {
                // Nothing to delete: the record is closed with what the check found.
                ShowOutcome(record, _journal.FinishRollback(record, plan, string.Empty));
                return;
            }

            var result = await PreviewAndExecuteAsync(
                _journal.GetRollbackCommand(plan),
                GetText(UiTextKey.SetupRunsRollbackPreviewTitle, "Roll back the run {label}", new Dictionary<string, object?> { ["label"] = record.Label }));
            if (result is null)
            {
                return;
            }

            var output = string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput;
            ShowOutcome(record, _journal.FinishRollback(record, plan, output));
        }, nameof(RollBackAsync));

        /// <summary>Shows a check without a tenant, for the headless renders of the page.</summary>
        public void ShowCheckForDisplay(RollbackPlan plan)
        {
            if (Selected() is { } record)
            {
                ShowPlan(record, plan);
            }
        }

        private async Task<RollbackPlan?> CheckAsync(RunRecord? record)
        {
            if (record is null || _journal is null)
            {
                StatusMessage = GetText(UiTextKey.SetupRunsSelectStatus, "Select a run first.");
                return null;
            }

            // A run is rolled back only in the tenant it ran in. Names repeat between customers, so
            // the connection of another customer must never be searched for the objects of this run.
            if (!IsOfConnectedTenant(record))
            {
                _checked = null;
                _approvedState = null;
                CanRollBack = false;
                StatusMessage = GetText(
                    UiTextKey.SetupRunsOtherTenantStatus,
                    "This run belongs to another tenant ({tenant}). Connect to that tenant to roll it back.",
                    new Dictionary<string, object?> { ["tenant"] = record.TenantName.Length == 0 ? record.TenantId : record.TenantName });
                return null;
            }

            try
            {
                var plan = await _journal.PlanRollbackAsync(record, async script =>
                {
                    var result = await ExecutePowerShellCommandAsync(script, null, "ReadRunObjects", allowThrottleRetry: true);
                    return string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput;
                });
                ShowPlan(record, plan);
                return plan;
            }
            catch (System.IO.InvalidDataException ex)
            {
                _checked = null;
                _approvedState = null;
                CanRollBack = false;
                StatusMessage = GetText(
                    UiTextKey.SetupRunsCheckFailedStatus,
                    "The tenant could not be read: {details}",
                    new Dictionary<string, object?> { ["details"] = ex.Message });
                return null;
            }
        }

        private void ShowPlan(RunRecord record, RollbackPlan plan)
        {
            _checked = plan;
            _approvedState = RunJournal.ApprovedState(plan);
            _checkedRunId = record.Id;
            var lines = Header(record);
            lines.Add(string.Empty);
            if (plan.Items.Count == 0)
            {
                lines.Add(GetText(UiTextKey.SetupRunsNothingToRemove, "Nothing of this run is left to remove."));
            }

            foreach (var item in plan.Items)
            {
                var values = new Dictionary<string, object?>
                {
                    ["type"] = TypeText(item.Object.Type),
                    ["name"] = item.Object.Name,
                    ["reason"] = ReasonText(item)
                };
                lines.Add(item.Action switch
                {
                    RollbackAction.Delete => GetText(UiTextKey.SetupRunsPlanDelete, "Will be removed: {type} {name}", values),
                    RollbackAction.AlreadyGone => GetText(UiTextKey.SetupRunsPlanGone, "Already gone: {type} {name}", values),
                    _ => GetText(UiTextKey.SetupRunsPlanBlocked, "Stays: {type} {name} ({reason})", values)
                });
            }

            Details = string.Join(Environment.NewLine, lines);
            CanRollBack = plan.Items.Any(item => item.Action is RollbackAction.Delete or RollbackAction.AlreadyGone);
            StatusMessage = GetText(
                UiTextKey.SetupRunsCheckedStatus,
                "Checked: {remove} to remove, {stay} stay.",
                new Dictionary<string, object?>
                {
                    ["remove"] = plan.Items.Count(item => item.Action == RollbackAction.Delete),
                    ["stay"] = plan.Items.Count(item => item.Action == RollbackAction.Blocked)
                });
        }

        private void ShowOutcome(RunRecord record, IReadOnlyList<RollbackOutcome> outcomes)
        {
            var lines = Header(record);
            lines.Add(string.Empty);
            foreach (var outcome in outcomes)
            {
                var values = new Dictionary<string, object?>
                {
                    ["type"] = TypeText(outcome.Object.Type),
                    ["name"] = outcome.Object.Name,
                    ["message"] = outcome.Message
                };
                lines.Add(outcome.Removed
                    ? GetText(UiTextKey.SetupRunsOutcomeRemoved, "Removed: {type} {name}", values)
                    : GetText(UiTextKey.SetupRunsOutcomeLeft, "Not removed: {type} {name} ({message})", values));
            }

            _checked = null;
            _approvedState = null;
            CanRollBack = false;
            var id = record.Id;
            Refresh();
            SelectedRun = Runs.FirstOrDefault(row => row.Id == id);
            Details = string.Join(Environment.NewLine, lines);
            StatusMessage = GetText(
                UiTextKey.SetupRunsDoneStatus,
                "Rollback finished: {removed} removed, {left} left.",
                new Dictionary<string, object?>
                {
                    ["removed"] = outcomes.Count(outcome => outcome.Removed),
                    ["left"] = outcomes.Count(outcome => !outcome.Removed)
                });
        }

        private void ShowRecord()
        {
            var record = Selected();
            if (record is null)
            {
                Details = string.Empty;
                return;
            }

            var lines = Header(record);
            lines.Add(string.Empty);
            foreach (var item in record.Objects)
            {
                lines.Add(GetText(
                    item.Removed ? UiTextKey.SetupRunsObjectRemovedLine : UiTextKey.SetupRunsObjectLine,
                    item.Removed ? "{type}: {name} (removed)" : "{type}: {name}",
                    new Dictionary<string, object?> { ["type"] = TypeText(item.Type), ["name"] = item.Name }));
            }

            if (record.Objects.Count == 0)
            {
                lines.Add(GetText(UiTextKey.SetupRunsNoObjects, "This run created no object."));
            }

            Details = string.Join(Environment.NewLine, lines);
        }

        private List<string> Header(RunRecord record)
        {
            var lines = new List<string>
            {
                GetText(
                    UiTextKey.SetupRunsDetailHeader,
                    "{label}: {kind}, started {started}, {state}",
                    new Dictionary<string, object?>
                    {
                        ["label"] = record.Label,
                        ["kind"] = KindText(record.Kind),
                        ["started"] = record.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                        ["state"] = StateText(record.State)
                    })
            };
            if (record.State == RunState.Failed)
            {
                lines.Add(record.FailedStep is { } step
                    ? GetText(UiTextKey.SetupRunsDetailFailedStep, "Stopped at step {step}: {error}",
                        new Dictionary<string, object?> { ["step"] = step + 1, ["error"] = record.Error })
                    : GetText(UiTextKey.SetupRunsDetailFailed, "Failed: {error}",
                        new Dictionary<string, object?> { ["error"] = record.Error }));
            }

            return lines;
        }

        private bool IsOfConnectedTenant(RunRecord record)
            => !string.IsNullOrWhiteSpace(record.TenantId) &&
               string.Equals(record.TenantId, _sessionManager.TenantId, StringComparison.OrdinalIgnoreCase);

        private RunRecord? Selected() => SelectedRun is null ? null : _journal?.Find(SelectedRun.Id);

        private string KindText(RunKind kind) => kind == RunKind.Bulk
            ? GetText(UiTextKey.SetupRunsKindBulk, "Bulk operation")
            : GetText(UiTextKey.SetupRunsKindGuided, "Guided Setup");

        private string StateText(RunState state) => state switch
        {
            RunState.Failed => GetText(UiTextKey.SetupRunsStateFailed, "Failed"),
            RunState.Completed => GetText(UiTextKey.SetupRunsStateCompleted, "Completed"),
            RunState.RolledBack => GetText(UiTextKey.SetupRunsStateRolledBack, "Rolled back"),
            _ => GetText(UiTextKey.SetupRunsStateRunning, "Not finished")
        };

        private string TypeText(RunObjectType type) => type switch
        {
            RunObjectType.M365Group => GetText(UiTextKey.SetupRunsTypeGroup, "Microsoft 365 group"),
            RunObjectType.ResourceAccount => GetText(UiTextKey.SetupRunsTypeResourceAccount, "Resource account"),
            RunObjectType.CallQueue => GetText(UiTextKey.SetupRunsTypeCallQueue, "Call queue"),
            _ => GetText(UiTextKey.SetupRunsTypeAutoAttendant, "Auto attendant")
        };

        private string ReasonText(RollbackItem item) => item.Code switch
        {
            RunJournal.ReasonUnreadable => GetText(UiTextKey.SetupRunsReasonUnreadable, "the tenant could not be read for it"),
            RunJournal.ReasonAmbiguous => GetText(UiTextKey.SetupRunsReasonAmbiguous, "several objects have this name"),
            RunJournal.ReasonNoId => GetText(UiTextKey.SetupRunsReasonNoId, "the run recorded it without its ID; delete it on its own page"),
            RunJournal.ReasonOtherId => GetText(UiTextKey.SetupRunsReasonOtherId, "the object with this name is not the one the run created"),
            _ => item.Reason
        };
    }
}
