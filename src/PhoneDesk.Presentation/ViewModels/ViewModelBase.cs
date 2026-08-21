using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using PhoneDesk.Localization;
using PhoneDesk.Audit;
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

        protected string FormatError(string error)
            => GetText(
                UiTextKey.RuntimeErrorWithDetails,
                "Error: {error}",
                new Dictionary<string, object?> { ["error"] = error });

        private static string FormatFallback(
            string template,
            IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters is null || parameters.Count == 0)
            {
                return template;
            }

            var value = template;
            foreach (var parameter in parameters)
            {
                value = value.Replace(
                    "{" + parameter.Key + "}",
                    parameter.Value?.ToString() ?? string.Empty,
                    StringComparison.Ordinal);
            }

            return value;
        }

        /// <summary>
        /// Lazily-created throttle retry policy. Built from the shared <see cref="ILoggingService"/> so
        /// retry attempts land in the application log. Only consulted when a caller opts in via
        /// <c>allowThrottleRetry</c> (which also asserts the operation is idempotent).
        /// </summary>
        private IThrottleRetryPolicy? _throttleRetryPolicy;
        protected IThrottleRetryPolicy ThrottleRetryPolicy => _throttleRetryPolicy ??= new ThrottleRetryPolicy(_loggingService);

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
            // Check session expiry before executing commands that require a connection
            if (_sessionManager.IsSessionExpired && _sessionManager.IsSessionValid)
            {
                _sessionManager.ResetSession();
                await _errorHandlingService.HandleConnectionError(
                    "Session",
                    GetText(
                        UiTextKey.RuntimeSessionExpiredMessage,
                        "Your session has expired (24h timeout). Please reconnect to Teams and Microsoft Graph."));
                return PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.AuthSession,
                    GetText(UiTextKey.RuntimeSessionExpiredShort, "Session expired. Please reconnect."),
                    "ERROR: Session expired. Please reconnect.");
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
                OperationResult<string> result;
                if (allowThrottleRetry)
                {
                    // Idempotent read/query: auto-retry on throttling with backoff (logged by the policy).
                    result = await ThrottleRetryPolicy.ExecuteAsync(
                        async ct => PowerShellOperationResultMapper.Map(
                            await _powerShellContextService.ExecuteCommandWithDetailsAsync(command, environmentVariables, progress, ct)),
                        new ThrottleRetryContext(string.IsNullOrEmpty(context) ? "PowerShell operation" : context, isIdempotent: true),
                        cts.Token);
                }
                else
                {
                    var execution = await _powerShellContextService.ExecuteCommandWithDetailsAsync(command, environmentVariables, progress, cts.Token);
                    result = PowerShellOperationResultMapper.Map(execution);
                }

                // Persist an audit record for the attempt (out-of-band; never alters the operation).
                RecordAudit(
                    context,
                    environmentVariables,
                    allowThrottleRetry,
                    result.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure,
                    result.IsSuccess ? null : (result.ErrorMessage ?? result.RawOutput),
                    result.CorrelationId);

                // Surface a user-facing error only for a reportable failure (error marker without success marker).
                if (result.ShouldReportError)
                {
                    await _errorHandlingService.HandlePowerShellError(command, result.RawOutput, context);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                // User cancelled: this is not an error, so we deliberately do NOT raise the error dialog.
                // The runspace remains reusable, so the next operation runs normally.
                LogLocalized(
                    UiTextKey.RuntimeOperationCancelledLog,
                    "Operation cancelled: {context}",
                    LogLevel.Warning,
                    new Dictionary<string, object?> { ["context"] = context });
                StatusMessage = GetText(UiTextKey.RuntimeOperationCancelled, "Operation cancelled.");
                RecordAudit(context, environmentVariables, allowThrottleRetry, AuditOutcome.Cancelled, null, correlationId: null);
                return PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.Cancelled,
                    GetText(UiTextKey.RuntimeOperationCancelledByUser, "Operation cancelled by user."),
                    "ERROR: Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                RecordAudit(context, environmentVariables, allowThrottleRetry, AuditOutcome.Failure, ex.Message, correlationId: null);
                await _errorHandlingService.HandlePowerShellError(command, ex.Message, context);
                return PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.Unknown,
                    ex.Message,
                    $"ERROR: {ex.Message}");
            }
            finally
            {
                _operationCts = null;
                IsCancellable = false;
                ResetProgress();
            }
        }

        /// <summary>
        /// Appends an audit record for an operation attempt. No-op when no audit sink is wired (unit tests).
        /// Never throws: an audit failure is logged and swallowed so it cannot affect the operation. Secrets
        /// in <paramref name="environmentVariables"/> / <paramref name="errorDetail"/> are redacted by the sink.
        /// </summary>
        private void RecordAudit(
            string context,
            IReadOnlyDictionary<string, string>? environmentVariables,
            bool isReadOnly,
            AuditOutcome outcome,
            string? errorDetail,
            string? correlationId)
        {
            if (_auditLog is null)
            {
                return;
            }

            try
            {
                var variables = _sharedStateService?.Variables;
                var record = new AuditRecord
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Operator = _sessionManager.GraphAccount ?? _sessionManager.TeamsAccount,
                    TenantId = _sessionManager.TenantId,
                    TenantName = _sessionManager.TenantName,
                    Operation = string.IsNullOrWhiteSpace(context) ? "PowerShell operation" : context,
                    Target = BuildAuditTarget(variables),
                    Parameters = environmentVariables is { Count: > 0 }
                        ? new Dictionary<string, string>(environmentVariables)
                        : null,
                    Outcome = outcome,
                    ErrorDetail = Truncate(errorDetail, 2000),
                    CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
                    AppVersion = ConstantsService.Application.Version,
                    Kind = isReadOnly ? AuditKind.Read : AuditKind.Operation
                };

                _auditLog.Append(record);
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Audit log write failed: {ex.Message}", LogLevel.Warning);
            }
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

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "…";
        }

        protected async Task<bool> ValidatePrerequisites()
        {
            var validationResult = _validationService.ValidatePrerequisites();
            if (!validationResult.IsValid)
            {
                await _errorHandlingService.HandleValidationError(validationResult.GetErrorMessage(), GetType().Name);
                return false;
            }
            return true;
        }

        protected async Task<bool> ValidateVariables(Models.PhoneManagerVariables variables)
        {
            var validationResult = _validationService.ValidateVariables(variables);
            if (!validationResult.IsValid)
            {
                await _errorHandlingService.HandleValidationError(validationResult.GetErrorMessage(), GetType().Name);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Wraps an async operation with IsBusy tracking and error handling.
        /// Reduces the try/catch/finally boilerplate in every command method.
        /// </summary>
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
                _loggingService.Log($"Exception in {context}: {ex}", LogLevel.Error);
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
        protected async Task<OperationResult<string>?> PreviewAndExecuteAsync(string command, string context = "", Dictionary<string, string>? environmentVariables = null)
        {
            if (_dialogService != null && !(_sharedStateService?.SkipScriptPreview ?? false))
            {
                var confirmed = await _dialogService.ShowScriptPreviewAsync(
                    GetText(
                        UiTextKey.DialogPreviewTitle,
                        "Preview: {context}",
                        new Dictionary<string, object?> { ["context"] = context }),
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
            if (_dialogService != null && !(_sharedStateService?.SkipDeleteConfirmation ?? false))
            {
                var confirmed = await _dialogService.ShowConfirmationWithPreviewAsync(
                    GetText(
                        UiTextKey.DialogConfirmTitle,
                        "Confirm: {context}",
                        new Dictionary<string, object?> { ["context"] = context }),
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
