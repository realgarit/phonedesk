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
    /// <summary>One setting of the object on the page: what the tenant holds, and what the operator wants.</summary>
    public partial class EditFieldRow : ObservableObject
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Help { get; init; } = string.Empty;
        public string Current { get; init; } = string.Empty;
        public bool IsChoice { get; init; }
        public bool IsText => !IsChoice && !IsLocked;
        public bool ShowChoice => IsChoice && !IsLocked;
        public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
        public bool IsLocked { get; init; }
        public string LockReason { get; init; } = string.Empty;

        [ObservableProperty]
        private string _newValue = string.Empty;
    }

    /// <summary>
    /// Changes to a call queue or an auto attendant that exists. The page reads the object, shows
    /// each setting with its current value, lists what would change, and saves only that. The
    /// rules and scripts come from <see cref="ObjectEdit"/>, the same code the MCP server uses.
    /// </summary>
    public partial class EditSettingsViewModel : ViewModelBase
    {
        private EditObjectState? _state;
        private EditAssessment? _checked;
        private string? _approvedState;

        public ObservableCollection<EditFieldRow> Fields { get; } = new();

        /// <summary>0 for a call queue, 1 for an auto attendant.</summary>
        [ObservableProperty]
        private int _kindIndex;

        [ObservableProperty]
        private string _objectName = string.Empty;

        [ObservableProperty]
        private string _loadedTitle = string.Empty;

        [ObservableProperty]
        private string _notes = string.Empty;

        [ObservableProperty]
        private string _summary = string.Empty;

        [ObservableProperty]
        private bool _canApply;

        public EditSettingsViewModel(
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
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            // The call queue or auto attendant page sent the operator here with an object.
            if (sharedStateService?.EditRequest is { } request)
            {
                sharedStateService.EditRequest = null;
                KindIndex = request.Kind == EditObjectKind.AutoAttendant ? 1 : 0;
                ObjectName = request.Name;
            }
        }

        private EditObjectKind Kind => KindIndex == 1 ? EditObjectKind.AutoAttendant : EditObjectKind.CallQueue;

        public bool IsLoaded => _state is not null;
        public bool ShowEmptyState => _state is null;
        public bool HasNotes => !string.IsNullOrEmpty(Notes);
        public bool HasSummary => !string.IsNullOrEmpty(Summary);

        partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
        partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

        // Another object or kind makes what is on the page stale.
        partial void OnKindIndexChanged(int value) => Clear();
        partial void OnObjectNameChanged(string value) => InvalidateCheck();

        private void Clear()
        {
            _state = null;
            Fields.Clear();
            LoadedTitle = string.Empty;
            Notes = string.Empty;
            InvalidateCheck();
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(ShowEmptyState));
        }

        private void InvalidateCheck()
        {
            _checked = null;
            _approvedState = null;
            CanApply = false;
            Summary = string.Empty;
        }

        /// <summary>Reads the object and shows its settings. Changes nothing.</summary>
        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
        {
            var state = await ReadAsync();
            if (state is not null)
            {
                Show(state);
                StatusMessage = GetText(UiTextKey.EditSettingsLoadedStatus, "Read from the tenant. Change the values you want, then select Check.");
            }
        }, nameof(LoadAsync));

        /// <summary>Lists what would change. Sends nothing.</summary>
        [RelayCommand]
        private void Check()
        {
            if (_state is null)
            {
                StatusMessage = GetText(UiTextKey.EditSettingsReadFirstStatus, "Read the object first.");
                return;
            }

            ShowAssessment(ObjectEdit.Assess(_state, Wanted()));
        }

        /// <summary>
        /// Reads the object again and saves only when it still holds what the check compared with.
        /// Then it reads once more and reports each setting from that read.
        /// </summary>
        [RelayCommand]
        private Task ApplyAsync() => RunBusyAsync(async () =>
        {
            if (_checked is null || _approvedState is null)
            {
                return;
            }

            var wanted = Wanted();
            var fresh = await ReadAsync();
            if (fresh is null)
            {
                return;
            }

            var assessment = ObjectEdit.Assess(fresh, wanted);
            if (!string.Equals(_approvedState, ObjectEdit.ApprovedState(assessment), StringComparison.Ordinal))
            {
                // Someone changed the object since the check: the operator sees the new values first.
                Show(fresh, wanted);
                ShowAssessment(assessment);
                StatusMessage = GetText(
                    UiTextKey.EditSettingsChangedStatus,
                    "The object changed in the tenant after the check. Nothing was saved. Read the new list of changes, then apply again.");
                return;
            }

            var result = await PreviewAndExecuteAsync(
                ObjectEdit.GetApplyCommand(assessment),
                GetText(UiTextKey.EditSettingsPreviewTitle, "Change {name}", new Dictionary<string, object?> { ["name"] = fresh.Name }));
            if (result is null)
            {
                return;
            }

            var outcomes = ObjectEdit.ParseOutcome(assessment, string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
            var after = await ReadAsync();
            var open = after is null ? null : ObjectEdit.NotAsWanted(assessment, after).Select(change => change.Key).ToHashSet(StringComparer.Ordinal);
            var lines = new List<string>();
            foreach (var outcome in outcomes)
            {
                var values = new Dictionary<string, object?> { ["setting"] = LabelOf(outcome.Key, outcome.Label), ["message"] = outcome.Message };
                // A setting counts as saved when the script said so and the new read shows it.
                lines.Add(outcome.Saved && open?.Contains(outcome.Key) != true
                    ? GetText(UiTextKey.EditSettingsOutcomeSaved, "Saved: {setting}", values)
                    : outcome.Saved
                        ? GetText(UiTextKey.EditSettingsOutcomeNotShown, "Sent, but the tenant does not show it yet: {setting}", values)
                        : GetText(UiTextKey.EditSettingsOutcomeFailed, "Not saved: {setting} ({message})", values));
            }

            if (after is not null)
            {
                Show(after);
            }

            Summary = string.Join(Environment.NewLine, lines);
            StatusMessage = GetText(
                UiTextKey.EditSettingsDoneStatus,
                "{saved} of {total} settings saved.",
                new Dictionary<string, object?> { ["saved"] = outcomes.Count(outcome => outcome.Saved), ["total"] = outcomes.Count });
        }, nameof(ApplyAsync));

        /// <summary>Shows an object without a tenant, for the headless renders of the page.</summary>
        public void ShowForDisplay(EditObjectState state, IReadOnlyDictionary<string, string?> wanted)
        {
            Show(state, wanted);
            ShowAssessment(ObjectEdit.Assess(state, wanted));
        }

        private async Task<EditObjectState?> ReadAsync()
        {
            try
            {
                var result = await ExecutePowerShellCommandAsync(
                    ObjectEdit.GetReadCommand(Kind, ObjectName), null, "ReadObjectSettings", allowThrottleRetry: true);
                return ObjectEdit.Parse(Kind, string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException)
            {
                StatusMessage = GetText(
                    UiTextKey.EditSettingsReadFailedStatus,
                    "The object could not be read: {details}",
                    new Dictionary<string, object?> { ["details"] = ex.Message });
                return null;
            }
        }

        private Dictionary<string, string?> Wanted()
            => Fields.Where(row => !row.IsLocked).ToDictionary(row => row.Key, row => (string?)row.NewValue, StringComparer.Ordinal);

        private void Show(EditObjectState state, IReadOnlyDictionary<string, string?>? keep = null)
        {
            _state = state;
            Fields.Clear();
            foreach (var field in ObjectEdit.Fields(state.Kind))
            {
                var locked = state.Locked.TryGetValue(field.Key, out var reason);
                if (!locked && !state.Values.ContainsKey(field.Key))
                {
                    continue;
                }

                var current = locked ? string.Empty : state.Values[field.Key];
                var row = new EditFieldRow
                {
                    Key = field.Key,
                    Label = LabelOf(field.Key, field.Label),
                    Help = HelpOf(field.Key, field.Help),
                    Current = current,
                    IsChoice = field.Type is EditFieldType.Choice or EditFieldType.Bool,
                    Choices = field.Type == EditFieldType.Bool ? new[] { "true", "false" } : field.Choices,
                    IsLocked = locked,
                    LockReason = reason ?? string.Empty,
                    NewValue = keep is not null && keep.TryGetValue(field.Key, out var kept) && kept is not null ? kept : current
                };
                row.PropertyChanged += (_, _) => InvalidateCheck();
                Fields.Add(row);
            }

            LoadedTitle = state.Name;
            Notes = string.Join(Environment.NewLine, state.Notes);
            InvalidateCheck();
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(ShowEmptyState));
        }

        private void ShowAssessment(EditAssessment assessment)
        {
            _checked = assessment;
            _approvedState = ObjectEdit.ApprovedState(assessment);
            var lines = new List<string>();
            foreach (var blocker in assessment.Blockers)
            {
                lines.Add(GetText(UiTextKey.EditSettingsBlockedLine, "Cannot be done: {details}", new Dictionary<string, object?> { ["details"] = blocker.Message }));
            }

            foreach (var change in assessment.Changes)
            {
                lines.Add(GetText(
                    UiTextKey.EditSettingsChangeLine,
                    "{setting}: {old} → {new}",
                    new Dictionary<string, object?>
                    {
                        ["setting"] = LabelOf(change.Key, change.Label),
                        ["old"] = change.Old.Length == 0 ? GetText(UiTextKey.EditSettingsEmptyValue, "(empty)") : change.Old,
                        ["new"] = change.New.Length == 0 ? GetText(UiTextKey.EditSettingsEmptyValue, "(empty)") : change.New
                    }));
            }

            lines.AddRange(assessment.Warnings);
            if (assessment.Changes.Count == 0 && assessment.Blockers.Count == 0)
            {
                lines.Add(GetText(UiTextKey.EditSettingsNoChanges, "Nothing differs from the tenant. There is nothing to save."));
            }

            Summary = string.Join(Environment.NewLine, lines);
            CanApply = assessment.CanApply;
            StatusMessage = GetText(
                UiTextKey.EditSettingsCheckedStatus,
                "{count} setting(s) would change. Apply shows the script before it runs.",
                new Dictionary<string, object?> { ["count"] = assessment.Changes.Count });
        }

        private string Suffix(string key) => (Kind == EditObjectKind.CallQueue ? "Cq" : "Aa") + char.ToUpperInvariant(key[0]) + key[1..];

        private string LabelOf(string key, string fallback)
            => Enum.TryParse<UiTextKey>("EditLabel" + Suffix(key), out var id) ? GetText(id, fallback) : fallback;

        private string HelpOf(string key, string fallback)
            => Enum.TryParse<UiTextKey>("EditHelp" + Suffix(key), out var id) ? GetText(id, fallback) : fallback;
    }
}
