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
    /// <summary>A phone number of the inventory, as a row.</summary>
    public sealed class PhoneNumberListRow
    {
        public string Number { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string AssignedTo { get; init; } = string.Empty;
        public bool IsAssigned { get; init; }
    }

    /// <summary>
    /// The phone number inventory of the tenant, and the assignment of a number to an employee or a
    /// resource account: assign, move and unassign. The number always stays in the inventory. The
    /// read scripts, checks and change scripts come from <see cref="PhoneNumbers"/>, the same code
    /// the MCP server uses.
    /// </summary>
    public partial class PhoneNumbersViewModel : ViewModelBase
    {
        private PhoneNumberAssessment? _checked;
        private string? _approvedState;

        public ObservableCollection<PhoneNumberListRow> Numbers { get; } = new();

        public IReadOnlyList<string> TypeChoices { get; } = PhoneNumbers.Types;

        [ObservableProperty]
        private PhoneNumberListRow? _selectedNumber;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _unassignedOnly;

        [ObservableProperty]
        private bool _isLoaded;

        [ObservableProperty]
        private string _summary = string.Empty;

        [ObservableProperty]
        private bool _showAssignDialog;

        [ObservableProperty]
        private bool _showUnassignDialog;

        [ObservableProperty]
        private string _dialogNumber = string.Empty;

        [ObservableProperty]
        private string _dialogTarget = string.Empty;

        [ObservableProperty]
        private string? _dialogType;

        [ObservableProperty]
        private bool _allowReassign;

        [ObservableProperty]
        private string _dialogPreview = string.Empty;

        [ObservableProperty]
        private bool _canApply;

        public PhoneNumbersViewModel(
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
        }

        public bool ShowEmptyState => !IsLoaded;
        public bool ShowDialog => ShowAssignDialog || ShowUnassignDialog;
        public bool HasDialogPreview => !string.IsNullOrEmpty(DialogPreview);

        partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));
        partial void OnShowAssignDialogChanged(bool value) => OnPropertyChanged(nameof(ShowDialog));
        partial void OnShowUnassignDialogChanged(bool value) => OnPropertyChanged(nameof(ShowDialog));
        partial void OnDialogPreviewChanged(string value) => OnPropertyChanged(nameof(HasDialogPreview));

        // An input that changes makes the last check stale: Apply needs a new check.
        partial void OnDialogNumberChanged(string value) => InvalidateCheck();
        partial void OnDialogTargetChanged(string value) => InvalidateCheck();
        partial void OnDialogTypeChanged(string? value) => InvalidateCheck();
        partial void OnAllowReassignChanged(bool value) => InvalidateCheck();

        private void InvalidateCheck()
        {
            CanApply = false;
            _checked = null;
            _approvedState = null;
        }

        [RelayCommand]
        private async Task LoadAsync()
        {
            await RunBusyAsync(ReadListAsync, nameof(LoadAsync));
        }

        [RelayCommand]
        private void OpenAssign()
        {
            DialogNumber = SelectedNumber?.Number ?? string.Empty;
            DialogTarget = string.Empty;
            DialogType = string.IsNullOrEmpty(SelectedNumber?.Type) ? null : SelectedNumber!.Type;
            AllowReassign = false;
            DialogPreview = string.Empty;
            InvalidateCheck();
            ShowAssignDialog = true;
        }

        [RelayCommand]
        private void OpenUnassign()
        {
            if (SelectedNumber is null || !SelectedNumber.IsAssigned)
            {
                StatusMessage = GetText(UiTextKey.PhoneNumbersSelectAssignedStatus, "Select a number that is assigned first.");
                return;
            }

            DialogNumber = SelectedNumber.Number;
            DialogTarget = string.Empty;
            DialogPreview = string.Empty;
            InvalidateCheck();
            ShowUnassignDialog = true;
        }

        [RelayCommand]
        private void CloseDialog()
        {
            ShowAssignDialog = false;
            ShowUnassignDialog = false;
            DialogPreview = string.Empty;
            InvalidateCheck();
        }

        /// <summary>Reads the number and the accounts and shows what the change would do. Nothing is sent.</summary>
        [RelayCommand]
        private async Task CheckAsync()
        {
            await RunBusyAsync(async () => await PrepareAsync(), nameof(CheckAsync));
        }

        /// <summary>
        /// Runs the change that was checked. The number and the accounts are read again first: when
        /// they no longer are what the check showed, nothing runs and the new check is shown.
        /// </summary>
        [RelayCommand]
        private async Task ApplyAsync()
        {
            if (!CanApply || _approvedState is null)
            {
                return;
            }

            await RunBusyAsync(async () =>
            {
                var approved = _approvedState;
                if (!await PrepareAsync() || _checked is null || !string.Equals(approved, _approvedState, StringComparison.Ordinal))
                {
                    StatusMessage = GetText(
                        UiTextKey.PhoneNumbersChangedStatus,
                        "The number or an account changed after the check. Nothing was changed. Read the new check, then apply again.");
                    return;
                }

                var assessment = _checked;
                var target = ShowAssignDialog ? DialogTarget.Trim() : null;
                var title = ShowAssignDialog
                    ? GetText(UiTextKey.PhoneNumbersAssignAction, "Assign or move")
                    : GetText(UiTextKey.PhoneNumbersUnassignAction, "Unassign");
                ShowAssignDialog = false;
                ShowUnassignDialog = false;
                var result = await PreviewAndExecuteAsync(PhoneNumbers.GetChangeCommand(assessment), title);
                if (result is null)
                {
                    StatusMessage = GetText(UiTextKey.RuntimeOperationCancelledByUser, "Operation cancelled by user");
                    return;
                }

                // The tenant decides what happened: after a move that failed, the status says who has the number.
                var after = await ReadCheckAsync(assessment.Number, target);
                await ReadListAsync();
                if (after is null)
                {
                    // The change ran, but the number could not be read again: that is not a confirmation.
                    StatusMessage = GetText(
                        UiTextKey.PhoneNumbersUnverifiedStatus,
                        "The change was sent, but the number could not be read again, so the result is not confirmed. Read the list and check the number. {details}",
                        new Dictionary<string, object?> { ["details"] = result.Succeeded ? string.Empty : result.ErrorMessage ?? string.Empty });
                    return;
                }

                var problem = PhoneNumbers.Verify(assessment, after);
                StatusMessage = string.IsNullOrEmpty(problem)
                    ? GetText(UiTextKey.PhoneNumbersVerifiedStatus, "Saved. The number was read again and shows the change.")
                    : GetText(
                        UiTextKey.PhoneNumbersNotVerifiedStatus,
                        "The tenant does not show the change: {details}",
                        new Dictionary<string, object?> { ["details"] = result.Succeeded ? problem : problem + " " + result.ErrorMessage });
            }, nameof(ApplyAsync));
        }

        /// <summary>Shows rows without a tenant, for the headless renders of the page.</summary>
        public void ShowRowsForDisplay(string listOutput)
        {
            ShowRows(PhoneNumbers.ParseList(listOutput));
        }

        /// <summary>Shows the check of a state without a tenant, for the headless renders.</summary>
        public void CheckForDisplay(string checkOutput)
        {
            Prepare(PhoneNumbers.ParseCheck(checkOutput));
        }

        private async Task ReadListAsync()
        {
            var result = await ExecutePowerShellCommandAsync(
                PhoneNumbers.GetListCommand(SearchText, UnassignedOnly ? "unassigned" : null),
                null,
                "ListPhoneNumbers",
                allowThrottleRetry: true);
            try
            {
                ShowRows(PhoneNumbers.ParseList(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput));
                StatusMessage = Summary;
            }
            catch (System.IO.InvalidDataException ex)
            {
                StatusMessage = GetText(
                    UiTextKey.PhoneNumbersLoadFailedStatus,
                    "The phone numbers could not be read: {details}",
                    new Dictionary<string, object?> { ["details"] = string.IsNullOrWhiteSpace(result.ErrorMessage) ? ex.Message : result.ErrorMessage });
            }
        }

        private void ShowRows(IReadOnlyList<PhoneNumberRow> rows)
        {
            var selected = SelectedNumber?.Number;
            Numbers.Clear();
            foreach (var row in rows.OrderBy(row => row.Number, StringComparer.Ordinal))
            {
                Numbers.Add(new PhoneNumberListRow
                {
                    Number = row.Number,
                    Type = row.Type,
                    IsAssigned = row.IsAssigned,
                    Status = row.IsAssigned
                        ? GetText(UiTextKey.PhoneNumbersStatusAssigned, "Assigned")
                        : GetText(UiTextKey.PhoneNumbersStatusFree, "Free"),
                    AssignedTo = row.Owner?.Label ?? row.AssignedToId
                });
            }

            IsLoaded = true;
            SelectedNumber = Numbers.FirstOrDefault(row => row.Number == selected);
            Summary = GetText(
                UiTextKey.PhoneNumbersSummary,
                "{total} numbers: {assigned} assigned, {free} free.",
                new Dictionary<string, object?>
                {
                    ["total"] = rows.Count,
                    ["assigned"] = rows.Count(row => row.IsAssigned),
                    ["free"] = rows.Count(row => !row.IsAssigned)
                });
        }

        private async Task<PhoneNumberCheckState?> ReadCheckAsync(string number, string? target)
        {
            var result = await ExecutePowerShellCommandAsync(
                PhoneNumbers.GetCheckCommand(number, target), null, "CheckPhoneNumber", allowThrottleRetry: true);
            try
            {
                return PhoneNumbers.ParseCheck(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
            }
            catch (System.IO.InvalidDataException ex)
            {
                DialogPreview = Blocked(string.IsNullOrWhiteSpace(result.ErrorMessage) ? ex.Message : result.ErrorMessage);
                return null;
            }
        }

        private async Task<bool> PrepareAsync()
        {
            InvalidateCheck();
            try
            {
                var number = PhoneNumbers.RequireNumber(DialogNumber);
                var target = ShowAssignDialog ? PhoneNumbers.RequireIdentity(DialogTarget) : null;
                var state = await ReadCheckAsync(number, target);
                if (state is null)
                {
                    return false;
                }

                Prepare(state);
                return true;
            }
            catch (ArgumentException ex)
            {
                DialogPreview = Blocked(ex.Message);
                return false;
            }
        }

        private void Prepare(PhoneNumberCheckState state)
        {
            InvalidateCheck();
            PhoneNumberAssessment assessment;
            try
            {
                assessment = ShowUnassignDialog
                    ? PhoneNumbers.AssessUnassign(state, DialogNumber)
                    : PhoneNumbers.AssessAssign(state, DialogNumber, DialogType, AllowReassign);
            }
            catch (ArgumentException ex)
            {
                DialogPreview = Blocked(ex.Message);
                return;
            }

            var lines = new List<string>
            {
                Line(UiTextKey.PhoneNumbersPreviewNumber, "Number: {value}", assessment.Row is null
                    ? assessment.Number + " (" + GetText(UiTextKey.PhoneNumbersNotInInventory, "not in the inventory yet") + ")"
                    : $"{assessment.Number} ({assessment.Type})")
            };
            if (assessment.Row is { IsAssigned: true })
            {
                lines.Add(Line(UiTextKey.PhoneNumbersPreviewOwner, "Has the number now: {value}", assessment.CurrentOwner?.Label ?? assessment.Row.AssignedToId));
            }

            if (assessment.Target is not null)
            {
                lines.Add(Line(UiTextKey.PhoneNumbersPreviewTarget, "Gets the number: {value}", assessment.Target.Label));
            }

            lines.AddRange(assessment.Warnings);
            if (assessment.Blockers.Count > 0)
            {
                lines.Add(Blocked(string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message))));
            }
            else if (assessment.NoChange)
            {
                lines.Add(GetText(UiTextKey.PhoneNumbersPreviewNoChange, "The tenant has this already. There is nothing to save."));
            }
            else
            {
                lines.Add(assessment.Kind switch
                {
                    PhoneNumberChangeKind.Reassign => GetText(UiTextKey.PhoneNumbersPreviewMove, "Ready. The number is moved: the account that has it now loses it."),
                    PhoneNumberChangeKind.Unassign => GetText(UiTextKey.PhoneNumbersPreviewUnassign, "Ready. The number is taken off the account and stays in the inventory."),
                    _ => GetText(UiTextKey.PhoneNumbersPreviewAssign, "Ready. Apply shows the script before it runs.")
                });
                _checked = assessment;
                _approvedState = PhoneNumbers.ApprovedState(assessment);
                CanApply = true;
            }

            DialogPreview = string.Join(Environment.NewLine, lines);
        }

        private string Blocked(string? details)
            => GetText(
                UiTextKey.PhoneNumbersPreviewBlocked,
                "Cannot be done: {details}",
                new Dictionary<string, object?> { ["details"] = details ?? string.Empty });

        private string Line(UiTextKey key, string fallback, string value)
            => GetText(key, fallback, new Dictionary<string, object?> { ["value"] = value });
    }
}
