using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using PhoneDesk.Localization;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.ViewModels
{
    public partial class ViewModelBase : ObservableObject
    {
        protected readonly IPowerShellContextService _powerShellContextService;
        protected readonly IPowerShellCommandService _powerShellCommandService;
        protected readonly ILoggingService _loggingService;
        protected readonly ISessionManager _sessionManager;
        protected readonly INavigationService _navigationService;
        protected readonly IErrorHandlingService _errorHandlingService;
        protected readonly IValidationService _validationService;
        protected readonly ISharedStateService? _sharedStateService;
        protected readonly IDialogService? _dialogService;
        protected readonly ITranslationService? _translationService;
        private static readonly TranslationCatalog EmptyTranslationCatalog = new(new Dictionary<UiTextKey, string>());

        /// <summary>
        /// Optional persistent audit sink. When supplied by the composition root, every PowerShell-backed
        /// operation executed through <see cref="ExecutePowerShellCommandAsync(string, Dictionary{string, string}?, string, bool)"/>
        /// appends a record. Null in unit tests, where auditing is not under test.
        /// </summary>
        protected readonly IAuditLog? _auditLog;

        public TranslationCatalog Text { get; }

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _waitingMessage = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private string _logMessage = string.Empty;

        /// <summary>True while a cancellable PowerShell operation is running; drives the Cancel affordance.</summary>
        [ObservableProperty]
        private bool _isCancellable;

        /// <summary>Determinate progress value (0..100). Only meaningful when <see cref="IsProgressIndeterminate"/> is false.</summary>
        [ObservableProperty]
        private double _progressValue;

        /// <summary>True when the running operation reports no percentage and the UI should show an indeterminate bar.</summary>
        [ObservableProperty]
        private bool _isProgressIndeterminate = true;

        /// <summary>Human-readable progress line surfaced from native PowerShell <c>Write-Progress</c> records.</summary>
        [ObservableProperty]
        private string _progressText = string.Empty;

        /// <summary>
        /// Token source for the operation currently in flight. Cancelling it stops the PowerShell pipeline
        /// cooperatively (the runspace stays reusable). Null when no operation is running.
        /// </summary>
        private CancellationTokenSource? _operationCts;

        partial void OnIsBusyChanged(bool value)
        {
            if (!value)
            {
                WaitingMessage = string.Empty;
            }
        }

        /// <summary>
        /// Requests cancellation of the operation currently in flight. Safe to call when nothing is running.
        /// Cancellation is cooperative: the PowerShell pipeline is stopped and the persistent runspace is
        /// left open for the next command.
        /// </summary>
        [RelayCommand]
        protected void CancelOperation()
        {
            if (_operationCts is { IsCancellationRequested: false })
            {
                LogLocalized(UiTextKey.RuntimeCancellationRequestedLog, "Cancellation requested by user", LogLevel.Info);
                StatusMessage = GetText(UiTextKey.RuntimeCancelling, "Cancelling…");
                _operationCts.Cancel();
            }
        }

        private void ResetProgress()
        {
            ProgressValue = 0;
            IsProgressIndeterminate = true;
            ProgressText = string.Empty;
        }

        private void OnPowerShellProgress(PowerShellProgress update)
        {
            IsProgressIndeterminate = update.IsIndeterminate;
            ProgressValue = update.IsIndeterminate ? 0 : update.PercentComplete;
            ProgressText = update.DisplayText;
        }

        protected ViewModelBase(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService? sharedStateService = null,
            IDialogService? dialogService = null,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
        {
            _powerShellContextService = powerShellContextService;
            _powerShellCommandService = powerShellCommandService;
            _loggingService = loggingService;
            _sessionManager = sessionManager;
            _navigationService = navigationService;
            _errorHandlingService = errorHandlingService;
            _validationService = validationService;
            _sharedStateService = sharedStateService;
            _dialogService = dialogService;
            _auditLog = auditLog;
            _translationService = translationService;
            Text = translationService?.Text ?? EmptyTranslationCatalog;
        }

        protected string GetText(
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => _translationService?.Get(key, parameters) ?? FormatFallback(fallback, parameters);

        protected void UpdateStatus(string message)
        {
            StatusMessage = message;
            LogMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
        }

        protected void UpdateStatus(
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => UpdateStatus(GetText(key, fallback, parameters));

        protected void SetWaiting(
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => WaitingMessage = GetText(key, fallback, parameters);

        protected void LogLocalized(
            UiTextKey key,
            string fallback,
            LogLevel level,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => _loggingService.Log(GetText(key, fallback, parameters), level);

        protected void LogException(string context, Exception ex)
            => LogLocalized(
                UiTextKey.RuntimeExceptionLog,
                "Exception in {context}: {details}",
                LogLevel.Error,
                new Dictionary<string, object?>
                {
                    ["context"] = context,
                    ["details"] = ex
                });

        protected string FormatError(string error)
            => GetText(
                UiTextKey.RuntimeErrorWithDetails,
                "Error: {error}",
                new Dictionary<string, object?> { ["error"] = error });

        private static string FormatFallback(
            string template,
            IReadOnlyDictionary<string, object?>? parameters)
            => TranslationCatalog.FormatTemplate(template, parameters);

        /// <summary>
        /// Lazily-created throttle retry policy. Built from the shared <see cref="ILoggingService"/> so
        /// retry attempts land in the application log. Only consulted when a caller opts in via
        /// <c>allowThrottleRetry</c> (which also asserts the operation is idempotent).
        /// </summary>
        private IThrottleRetryPolicy? _throttleRetryPolicy;
        protected IThrottleRetryPolicy ThrottleRetryPolicy => _throttleRetryPolicy ??= new ThrottleRetryPolicy(_loggingService);

        private ITenantCommandRunner? _commandRunner;
        private ITenantCommandRunner CommandRunner => _commandRunner ??= new TenantCommandRunner(
            _powerShellContextService, _sessionManager, ThrottleRetryPolicy, _loggingService, _auditLog);

        protected Task<OperationResult<string>> ExecutePowerShellCommandAsync(string command, string context = "")
            => ExecutePowerShellCommandAsync(command, null, context, allowThrottleRetry: false);

        protected Task<OperationResult<string>> ExecutePowerShellCommandAsync(string command, Dictionary<string, string>? environmentVariables, string context = "")
            => ExecutePowerShellCommandAsync(command, environmentVariables, context, allowThrottleRetry: false);

        /// <summary>
        /// Executes a PowerShell command and maps the result. When <paramref name="allowThrottleRetry"/> is
        /// true the execution is wrapped in the throttle retry policy as an <b>idempotent</b> operation —
        /// callers must only opt in for reads/queries that are safe to re-run. Mutating operations leave it
        /// false, so a 429 surfaces the typed <see cref="OperationErrorCategory.Throttling"/> result instead
        /// of being silently re-executed. With <paramref name="allowThrottleRetry"/> false the behavior is
        /// unchanged from the single-shot path.
        /// </summary>
        protected async Task<OperationResult<string>> ExecutePowerShellCommandAsync(string command, Dictionary<string, string>? environmentVariables, string context, bool allowThrottleRetry)
        {
            // Connected to another tenant than expected, or to two tenants: every page sends nothing.
            // Only what the operator needs to correct the connections still runs.
            if (!IdentityRecoveryContexts.Contains(context) && IdentityProblem() is { } identityProblem)
            {
                StatusMessage = identityProblem;
                return PowerShellOperationResultMapper.Failure(OperationErrorCategory.AuthSession, identityProblem, string.Empty);
            }

            using var cts = new CancellationTokenSource();
            _operationCts = cts;
            IsCancellable = true;
            ResetProgress();

            // Progress<T> marshals callbacks back onto the captured (UI) SynchronizationContext, so the
            // observable progress properties are always updated on the UI thread.
            var progress = new Progress<PowerShellProgress>(OnPowerShellProgress);

            try
            {
                // The shared runner owns the session-expiry check, throttle retry, result mapping and the
                // audit record. This method adds only what needs the UI: dialogs, status and cancellation.
                var outcome = await CommandRunner.RunAsync(
                    new TenantCommandRequest(command, context)
                    {
                        EnvironmentVariables = environmentVariables,
                        IsIdempotentRead = allowThrottleRetry,
                        Target = BuildAuditTarget(_sharedStateService?.Variables),
                        Progress = progress
                    },
                    cts.Token,
                    new TenantCommandRunnerMessages(
                        GetText(UiTextKey.RuntimeSessionExpiredShort, "Session expired. Please reconnect."),
                        GetText(UiTextKey.RuntimeOperationCancelledByUser, "Operation cancelled by user."),
                        error => GetText(
                            UiTextKey.RuntimeAuditLogWriteFailedLog,
                            "Audit log write failed: {error}",
                            new Dictionary<string, object?> { ["error"] = error })));

                switch (outcome.Status)
                {
                    case TenantCommandStatus.SessionExpired:
                        await _errorHandlingService.HandleConnectionError(
                            "Session",
                            GetText(
                                UiTextKey.RuntimeSessionExpiredMessage,
                                "Your session has expired (24h timeout). Please reconnect to Teams and Microsoft Graph."));
                        break;
                    case TenantCommandStatus.Cancelled:
                        // User cancelled: this is not an error, so we deliberately do NOT raise the error dialog.
                        // The runspace remains reusable, so the next operation runs normally.
                        LogLocalized(
                            UiTextKey.RuntimeOperationCancelledLog,
                            "Operation cancelled: {context}",
                            LogLevel.Warning,
                            new Dictionary<string, object?> { ["context"] = context });
                        StatusMessage = GetText(UiTextKey.RuntimeOperationCancelled, "Operation cancelled.");
                        break;
                    case TenantCommandStatus.Faulted:
                        await _errorHandlingService.HandlePowerShellError(
                            command, outcome.Exception?.Message ?? outcome.Result.ErrorMessage ?? string.Empty, context);
                        break;
                    default:
                        // Surface a user-facing error only for a reportable failure (error marker without success marker).
                        if (outcome.Result.ShouldReportError)
                        {
                            await _errorHandlingService.HandlePowerShellError(command, outcome.Result.RawOutput, context);
                        }
                        break;
                }

                return outcome.Result;
            }
            finally
            {
                _operationCts = null;
                IsCancellable = false;
                ResetProgress();
            }
        }

        /// <summary>The operations that run with an identity problem: the sign-ins, the sign-outs and the reads that show the problem.</summary>
        private static readonly HashSet<string> IdentityRecoveryContexts = new(StringComparer.Ordinal)
        {
            "CheckModules", "ConnectTeams", "ConnectGraph", "DisconnectTeams", "DisconnectGraph", "ReadConnectionIdentity"
        };

        /// <summary>The message for the operator when the connections are not the expected ones, or null.</summary>
        private string? IdentityProblem()
        {
            var issue = _validationService?.ValidatePrerequisites()?.Issues
                .FirstOrDefault(item => item.Code == ValidationErrorCode.TenantIdentityMismatch);
            return issue is null ? null : GetValidationErrorMessage(issue);
        }

        /// <summary>Best-effort identifier of the object(s) an operation acted on, from the current variables.</summary>
        private static string? BuildAuditTarget(Models.PhoneManagerVariables? variables)
        {
            if (variables is null || string.IsNullOrWhiteSpace(variables.Customer))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(variables.CustomerGroupName)
                ? variables.Customer
                : $"{variables.Customer}-{variables.CustomerGroupName}";
        }

        protected string GetValidationErrorMessage(ValidationResult result)
        {
            var messages = new List<string>(result.Issues.Count);
            foreach (var issue in result.Issues)
            {
                messages.Add(GetValidationErrorMessage(issue));
            }

            return string.Join(Environment.NewLine, messages);
        }

        protected string GetValidationErrorMessage(ValidationIssue issue)
            => issue.Code switch
            {
                ValidationErrorCode.ModulesNotChecked => GetText(
                    UiTextKey.ValidationModulesNotChecked,
                    "PowerShell modules have not been checked. Please check modules first."),
                ValidationErrorCode.TeamsNotConnected => GetText(
                    UiTextKey.ValidationTeamsNotConnected,
                    "Not connected to Microsoft Teams. Please connect to Teams first."),
                ValidationErrorCode.GraphNotConnected => GetText(
                    UiTextKey.ValidationGraphNotConnected,
                    "Not connected to Microsoft Graph. Please connect to Graph first."),
                ValidationErrorCode.TenantIdentityMismatch => GetText(
                    UiTextKey.ValidationTenantIdentityMismatch,
                    "The connections are not the expected ones. Nothing is sent to the tenant. Open Readiness Check for the details."),
                ValidationErrorCode.CustomerNameRequired => GetText(
                    UiTextKey.ValidationCustomerNameRequired,
                    "Customer name is required."),
                ValidationErrorCode.CustomerGroupNameRequired => GetText(
                    UiTextKey.ValidationCustomerGroupNameRequired,
                    "Customer group name is required."),
                ValidationErrorCode.MicrosoftFallbackDomainRequired => GetText(
                    UiTextKey.ValidationMicrosoftFallbackDomainRequired,
                    "Microsoft fallback domain is required."),
                ValidationErrorCode.MicrosoftFallbackDomainInvalid => GetText(
                    UiTextKey.ValidationMicrosoftFallbackDomainInvalid,
                    "Microsoft fallback domain is invalid. Enter a domain such as contoso.onmicrosoft.com."),
                ValidationErrorCode.CustomerLegalNameRequired => GetText(
                    UiTextKey.ValidationCustomerLegalNameRequired,
                    "Customer legal name is required."),
                ValidationErrorCode.LanguageIdRequired => GetText(
                    UiTextKey.ValidationLanguageIdRequired,
                    "Language ID is required."),
                ValidationErrorCode.TimeZoneIdRequired => GetText(
                    UiTextKey.ValidationTimeZoneIdRequired,
                    "Time zone ID is required."),
                ValidationErrorCode.UsageLocationRequired => GetText(
                    UiTextKey.ValidationUsageLocationRequired,
                    "Usage location is required."),
                ValidationErrorCode.ResourceAccountPhoneNumberRequired => GetText(
                    UiTextKey.ValidationResourceAccountPhoneNumberRequired,
                    "Resource account phone number is required."),
                ValidationErrorCode.PhoneNumberTypeRequired => GetText(
                    UiTextKey.ValidationPhoneNumberTypeRequired,
                    "Phone number type is required."),
                ValidationErrorCode.DefaultCallFlowGreetingTextRequired => GetText(
                    UiTextKey.ValidationDefaultCallFlowGreetingTextRequired,
                    "Default call flow greeting text is required when using Text-to-Speech."),
                ValidationErrorCode.DefaultCallFlowAudioFileRequired => GetText(
                    UiTextKey.ValidationDefaultCallFlowAudioFileRequired,
                    "Default call flow audio file is required when using Audio File."),
                ValidationErrorCode.AfterHoursCallFlowGreetingTextRequired => GetText(
                    UiTextKey.ValidationAfterHoursCallFlowGreetingTextRequired,
                    "After hours call flow greeting text is required when using Text-to-Speech."),
                ValidationErrorCode.AfterHoursCallFlowAudioFileRequired => GetText(
                    UiTextKey.ValidationAfterHoursCallFlowAudioFileRequired,
                    "After hours call flow audio file is required when using Audio File."),
                ValidationErrorCode.HolidayNameSuffixRequired => GetText(
                    UiTextKey.ValidationHolidayNameSuffixRequired,
                    "Holiday name suffix is required."),
                ValidationErrorCode.HolidayGreetingPromptRequired => GetText(
                    UiTextKey.ValidationHolidayGreetingPromptRequired,
                    "Holiday greeting prompt is required."),
                ValidationErrorCode.HolidayDateInPast => GetText(
                    UiTextKey.ValidationHolidayDateInPast,
                    "Holiday date cannot be in the past."),
                ValidationErrorCode.OpeningHoursInvalid => GetText(
                    UiTextKey.ValidationOpeningHoursInvalid,
                    "Opening hours are invalid. Each start time must be before its end time."),
                ValidationErrorCode.OpeningDaysRequired => GetText(
                    UiTextKey.ValidationOpeningDaysRequired,
                    "Select at least one open day in the day plan."),
                _ => issue.Message
            };

        protected async Task<bool> ValidatePrerequisites()
        {
            var validationResult = _validationService.ValidatePrerequisites();
            if (!validationResult.IsValid)
            {
                await _errorHandlingService.HandleValidationError(
                    GetValidationErrorMessage(validationResult),
                    GetType().Name);
                return false;
            }
            return true;
        }

        protected async Task<bool> ValidateVariables(Models.PhoneManagerVariables variables)
        {
            var validationResult = _validationService.ValidateVariables(variables);
            if (!validationResult.IsValid)
            {
                await _errorHandlingService.HandleValidationError(
                    GetValidationErrorMessage(validationResult),
                    GetType().Name);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Wraps an async operation with IsBusy tracking and error handling.
        /// Reduces the try/catch/finally boilerplate in every command method.
        /// </summary>
/// <summary>
        /// Reads the tenant for a dry-run plan and returns the plan with the checks against it:
        /// what exists already, free licenses, and whether the phone number is free. The script
        /// only reads. When the tenant cannot be read the plan comes back as it was.
        /// </summary>
        protected async Task<PhoneDesk.Planning.DryRunPlan> CheckPlanAgainstTenantAsync(
            PhoneDesk.Planning.DryRunPlan plan, IReadOnlyList<PhoneDesk.Models.IPhoneManagerVariables> sources)
        {
            var result = await ExecutePowerShellCommandAsync(
                _powerShellCommandService.GetTenantPreflightCommand(sources), null, "PreviewTenantCheck", allowThrottleRetry: true);
            try
            {
                var state = PhoneDesk.Operations.TenantPreflight.Parse(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
                var live = PhoneDesk.Operations.TenantPreflight.Apply(plan, sources, state);
                var checks = live.Entries.SelectMany(entry => entry.PreflightChecks).ToList();
                StatusMessage = GetText(
                    UiTextKey.PlanTenantCheckDoneStatus,
                    "Tenant checked: {fail} check(s) would fail, {warning} warning(s).",
                    new Dictionary<string, object?>
                    {
                        ["fail"] = checks.Count(check => check.IsFail),
                        ["warning"] = checks.Count(check => check.IsWarning)
                    });
                return live;
            }
            catch (System.IO.InvalidDataException ex)
            {
                StatusMessage = GetText(
                    UiTextKey.PlanTenantCheckFailedStatus,
                    "The tenant could not be read for the preview: {details}",
                    new Dictionary<string, object?> { ["details"] = string.IsNullOrWhiteSpace(result.ErrorMessage) ? ex.Message : result.ErrorMessage });
                return plan;
            }
        }

        protected async Task RunBusyAsync(Func<Task> action, string context = "", string? waitingMessage = null)
        {
            try
            {
                WaitingMessage = waitingMessage ?? string.Empty;
                IsBusy = true;
                await action();
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogException(context, ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        protected void NavigateTo(string page)
        {
            _navigationService.NavigateTo(page);
        }

        protected void NavigateToVariables()
        {
            NavigateTo(Services.ConstantsService.Pages.Variables);
        }

        protected void NavigateToM365Groups()
        {
            NavigateTo(Services.ConstantsService.Pages.M365Groups);
        }

        protected void NavigateToCallQueues()
        {
            NavigateTo(Services.ConstantsService.Pages.CallQueues);
        }

        protected void NavigateToAutoAttendants()
        {
            NavigateTo(Services.ConstantsService.Pages.AutoAttendants);
        }

        protected void NavigateToHolidays()
        {
            NavigateTo(Services.ConstantsService.Pages.Holidays);
        }

        protected void NavigateToGetStarted()
        {
            NavigateTo(Services.ConstantsService.Pages.GetStarted);
        }

        protected void NavigateToWelcome()
        {
            NavigateTo(Services.ConstantsService.Pages.Welcome);
        }

        /// <summary>
        /// Shows a script preview dialog and executes the command if the user confirms.
        /// Returns the execution result, or null if the user cancelled.
        /// </summary>
        /// <summary>
        /// Adds the tenant a change goes to, so an operator who works for several customers sees it
        /// in every confirmation. Without a connection the title stays as it is.
        /// </summary>
        protected string WithTenant(string title)
        {
            var tenant = string.IsNullOrWhiteSpace(_sessionManager.TenantName) ? _sessionManager.TenantId : _sessionManager.TenantName;
            return string.IsNullOrWhiteSpace(tenant)
                ? title
                : GetText(
                    UiTextKey.DialogTitleWithTenant,
                    "{title} (tenant: {tenant})",
                    new Dictionary<string, object?> { ["title"] = title, ["tenant"] = tenant });
        }

        protected async Task<OperationResult<string>?> PreviewAndExecuteAsync(string command, string context = "", Dictionary<string, string>? environmentVariables = null)
        {
            // The change is shown for the profile in use: it runs only in that profile's session.
            using var scope = PhoneDesk.Operations.SessionScope.Expect(_powerShellContextService.SessionKey);
            if (_dialogService != null && !(_sharedStateService?.SkipScriptPreview ?? false))
            {
                var confirmed = await _dialogService.ShowScriptPreviewAsync(
                    WithTenant(GetText(
                        UiTextKey.DialogPreviewTitle,
                        "Preview: {context}",
                        new Dictionary<string, object?> { ["context"] = context })),
                    command);
                if (!confirmed)
                {
                    LogLocalized(
                        UiTextKey.RuntimeScriptPreviewCancelledLog,
                        "User cancelled script preview for: {context}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["context"] = context });
                    return null;
                }
            }

            return await ExecutePowerShellCommandAsync(command, environmentVariables, context);
        }

        /// <summary>
        /// Shows a confirmation dialog with script preview for destructive operations.
        /// Returns the execution result, or null if the user cancelled.
        /// </summary>
        protected async Task<OperationResult<string>?> ConfirmAndExecuteAsync(string command, string confirmMessage, string context = "", Dictionary<string, string>? environmentVariables = null)
        {
            using var scope = PhoneDesk.Operations.SessionScope.Expect(_powerShellContextService.SessionKey);
            if (_dialogService != null && !(_sharedStateService?.SkipDeleteConfirmation ?? false))
            {
                var confirmed = await _dialogService.ShowConfirmationWithPreviewAsync(
                    WithTenant(GetText(
                        UiTextKey.DialogConfirmTitle,
                        "Confirm: {context}",
                        new Dictionary<string, object?> { ["context"] = context })),
                    confirmMessage,
                    command);
                if (!confirmed)
                {
                    LogLocalized(
                        UiTextKey.RuntimeDestructiveOperationCancelledLog,
                        "User cancelled destructive operation: {context}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["context"] = context });
                    return null;
                }
            }

            return await ExecutePowerShellCommandAsync(command, environmentVariables, context);
        }
    }
}
