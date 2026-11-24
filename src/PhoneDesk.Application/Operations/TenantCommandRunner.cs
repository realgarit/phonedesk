using PhoneDesk.Audit;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

/// <summary>One PowerShell-backed tenant operation to run.</summary>
/// <param name="Command">The script text.</param>
/// <param name="Operation">Short operation name, recorded in the audit log.</param>
public sealed record TenantCommandRequest(string Command, string Operation)
{
    /// <summary>Values passed to the script through process environment variables (for example a token).</summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// True only for reads that are safe to run again. Such a command is retried when the service
    /// throttles it, and it is audited as a read.
    /// </summary>
    public bool IsIdempotentRead { get; init; }

    /// <summary>The object the operation acts on, recorded in the audit log.</summary>
    public string? Target { get; init; }

    public IProgress<PowerShellProgress>? Progress { get; init; }
}

public enum TenantCommandStatus
{
    /// <summary>The command ran. The result says whether it succeeded.</summary>
    Completed,

    /// <summary>The 24-hour session expired. Nothing ran and the session was reset.</summary>
    SessionExpired,

    Cancelled,

    /// <summary>The host threw before a result was available.</summary>
    Faulted
}

public sealed record TenantCommandOutcome(
    TenantCommandStatus Status,
    OperationResult<string> Result,
    Exception? Exception = null);

/// <summary>Texts the runner puts into results and the log. A caller with a UI language supplies translations.</summary>
public sealed record TenantCommandRunnerMessages(
    string SessionExpired,
    string Cancelled,
    Func<string, string> AuditWriteFailed)
{
    public static TenantCommandRunnerMessages English { get; } = new(
        "Session expired. Please reconnect.",
        "Operation cancelled by user.",
        error => $"Audit log write failed: {error}");
}

public interface ITenantCommandRunner
{
    Task<TenantCommandOutcome> RunAsync(
        TenantCommandRequest request,
        CancellationToken cancellationToken = default,
        TenantCommandRunnerMessages? messages = null);
}

/// <summary>
/// Runs a tenant command the same way for every head: session-expiry check, throttle retry for
/// reads, typed result mapping, and one audit record for each attempt. It never shows UI and never
/// throws for a failed command.
/// </summary>
public sealed class TenantCommandRunner : ITenantCommandRunner
{
    private const int MaxAuditErrorLength = 2000;

    private readonly IPowerShellContextService _powerShell;
    private readonly ISessionManager _session;
    private readonly IThrottleRetryPolicy _retryPolicy;
    private readonly ILoggingService _logging;
    private readonly IAuditLog? _auditLog;

    public TenantCommandRunner(
        IPowerShellContextService powerShell,
        ISessionManager session,
        IThrottleRetryPolicy retryPolicy,
        ILoggingService logging,
        IAuditLog? auditLog = null)
    {
        _powerShell = powerShell;
        _session = session;
        _retryPolicy = retryPolicy;
        _logging = logging;
        _auditLog = auditLog;
    }

    public async Task<TenantCommandOutcome> RunAsync(
        TenantCommandRequest request,
        CancellationToken cancellationToken = default,
        TenantCommandRunnerMessages? messages = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        messages ??= TenantCommandRunnerMessages.English;

        if (_session.IsSessionExpired && _session.IsSessionValid)
        {
            _session.ResetSession();
            return new TenantCommandOutcome(
                TenantCommandStatus.SessionExpired,
                PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.AuthSession,
                    messages.SessionExpired,
                    "ERROR: Session expired. Please reconnect."));
        }

        try
        {
            OperationResult<string> result;
            if (request.IsIdempotentRead)
            {
                result = await _retryPolicy.ExecuteAsync(
                    async ct => PowerShellOperationResultMapper.Map(
                        await _powerShell.ExecuteCommandWithDetailsAsync(
                            request.Command, request.EnvironmentVariables, request.Progress, ct)),
                    new ThrottleRetryContext(OperationName(request), isIdempotent: true),
                    cancellationToken);
            }
            else
            {
                var execution = await _powerShell.ExecuteCommandWithDetailsAsync(
                    request.Command, request.EnvironmentVariables, request.Progress, cancellationToken);
                result = PowerShellOperationResultMapper.Map(execution);
            }

            RecordAudit(
                request,
                result.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure,
                result.IsSuccess ? null : (result.ErrorMessage ?? result.RawOutput),
                result.CorrelationId,
                messages);

            return new TenantCommandOutcome(TenantCommandStatus.Completed, result);
        }
        catch (OperationCanceledException)
        {
            RecordAudit(request, AuditOutcome.Cancelled, null, null, messages);
            return new TenantCommandOutcome(
                TenantCommandStatus.Cancelled,
                PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.Cancelled,
                    messages.Cancelled,
                    "ERROR: Operation cancelled by user."));
        }
        catch (Exception ex)
        {
            RecordAudit(request, AuditOutcome.Failure, ex.Message, null, messages);
            return new TenantCommandOutcome(
                TenantCommandStatus.Faulted,
                PowerShellOperationResultMapper.Failure(
                    OperationErrorCategory.Unknown,
                    ex.Message,
                    $"ERROR: {ex.Message}"),
                ex);
        }
    }

    private static string OperationName(TenantCommandRequest request)
        => string.IsNullOrWhiteSpace(request.Operation) ? "PowerShell operation" : request.Operation;

    private void RecordAudit(
        TenantCommandRequest request,
        AuditOutcome outcome,
        string? errorDetail,
        string? correlationId,
        TenantCommandRunnerMessages messages)
    {
        if (_auditLog is null)
        {
            return;
        }

        try
        {
            _auditLog.Append(new AuditRecord
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operator = _session.GraphAccount ?? _session.TeamsAccount,
                TenantId = _session.TenantId,
                TenantName = _session.TenantName,
                Operation = OperationName(request),
                Target = request.Target,
                Parameters = request.EnvironmentVariables is { Count: > 0 }
                    ? new Dictionary<string, string>(request.EnvironmentVariables)
                    : null,
                Outcome = outcome,
                ErrorDetail = Truncate(errorDetail),
                CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
                AppVersion = ConstantsService.Application.Version,
                Kind = request.IsIdempotentRead ? AuditKind.Read : AuditKind.Operation
            });
        }
        catch (Exception ex)
        {
            // An audit failure must never change the outcome of the operation.
            _logging.Log(messages.AuditWriteFailed(ex.Message), LogLevel.Warning);
        }
    }

    private static string? Truncate(string? value)
        => string.IsNullOrEmpty(value) || value.Length <= MaxAuditErrorLength
            ? value
            : value[..MaxAuditErrorLength] + "…";
}
