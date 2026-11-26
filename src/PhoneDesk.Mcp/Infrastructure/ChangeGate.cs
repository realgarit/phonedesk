using System.Security.Cryptography;
using PhoneDesk.Operations;
using PhoneDesk.Services;

namespace PhoneDesk.Mcp;

/// <summary>One change a tool wants to make in the tenant.</summary>
internal sealed record ChangeRequest(string Operation, string Target, string Script, string Summary)
{
    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// What the tenant held when the plan was shown, for a script that reads its input from the
    /// tenant when it runs. Such a script stays the same when the tenant changes, so the script
    /// alone cannot tell that the user agreed to something else. The plan runs only while this
    /// text is the same as at the time of the plan.
    /// </summary>
    public string? ApprovedState { get; init; }

    /// <summary>Decides whether the script result counts as success. Default: a SUCCESS line and no error.</summary>
    public Func<OperationResult<string>, bool> IsSuccess { get; init; } = result => result.Succeeded;

    /// <summary>Runs after a successful change, for example to record a created object.</summary>
    public Action<OperationResult<string>>? OnSuccess { get; init; }

    /// <summary>
    /// Checks the tenant after the script reported an error. Returns true when the change was made
    /// all the same. A script can fail in a late step after its main work is done; the tenant, not
    /// the script's last line, decides what happened.
    /// </summary>
    public Func<OperationResult<string>, CancellationToken, Task<bool>>? ChangeWasMadeDespiteError { get; init; }

    /// <summary>Runs after a successful change, for work that must read the tenant.</summary>
    public Func<OperationResult<string>, CancellationToken, Task>? OnSuccessAsync { get; init; }

    /// <summary>Runs after a failed change.</summary>
    public Action<OperationResult<string>>? OnFailure { get; init; }

    /// <summary>Runs after a failed change, for work that must read the tenant.</summary>
    public Func<OperationResult<string>, CancellationToken, Task>? OnFailureAsync { get; init; }

    /// <summary>
    /// Writes a backup of only the objects this change touches. It is used when the full tenant
    /// backup cannot be taken: the change then runs when this backup is complete, and not otherwise.
    /// </summary>
    public Func<CancellationToken, Task<ScopedBackupInfo>>? ScopedBackupAsync { get; init; }
}

/// <summary>
/// The safety gate every change passes through. A change tool is always called twice: the first
/// call stores a plan and returns the exact script; the second call, with the plan ID, runs it.
/// The gate runs a plan only when the script is still the same, takes a tenant backup first, and
/// uses each plan once.
/// </summary>
internal sealed class ChangeGate
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);

    private readonly ITenantCommandRunner _runner;
    private readonly TenantGateway _tenant;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredPlan> _plans = new(StringComparer.Ordinal);

    public ChangeGate(ITenantCommandRunner runner, TenantGateway tenant, TimeProvider time)
    {
        _runner = runner;
        _tenant = tenant;
        _time = time;
    }

    public async Task<object> PlanOrApplyAsync(ChangeRequest request, string? planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            throw new InvalidOperationException("No script could be built for this operation.");
        }

        var fingerprint = GuidedSetupScriptFactory.ComputeFingerprint(request.Script);
        var stateFingerprint = request.ApprovedState is null
            ? null
            : GuidedSetupScriptFactory.ComputeFingerprint(request.ApprovedState);
        if (string.IsNullOrWhiteSpace(planId))
        {
            return Plan(request, fingerprint, stateFingerprint);
        }

        TakePlan(planId, request.Operation, fingerprint, stateFingerprint);
        _tenant.RequireReady();

        BackupInfo? backup = null;
        ScopedBackupInfo? scopedBackup = null;
        string? fullBackupError = null;
        try
        {
            backup = await _tenant.EnsureBackupAsync(cancellationToken);
        }
        catch (InvalidDataException ex) when (request.ScopedBackupAsync is not null)
        {
            // One query of the full snapshot failed or warned. The change may still run when everything
            // it touches can be backed up; when that fails too, nothing runs.
            fullBackupError = ex.Message;
            try
            {
                scopedBackup = await request.ScopedBackupAsync(cancellationToken);
            }
            catch (Exception scoped) when (scoped is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The change did not run. The tenant backup failed: " + ex.Message +
                    " The backup of the objects this change touches failed too: " + scoped.Message);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                "The change did not run because the tenant backup failed: " + ex.Message +
                $" Fix the cause, or set {McpEnvironment.SkipBackupVariable}=1 to run changes without a backup.");
        }

        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(request.Script, request.Operation)
            {
                EnvironmentVariables = request.EnvironmentVariables,
                Target = request.Target
            },
            cancellationToken);
        var result = outcome.Result;
        var success = outcome.Status == TenantCommandStatus.Completed && request.IsSuccess(result);
        _tenant.InvalidateTopology();
        string? note = null;
        if (!success && outcome.Status is TenantCommandStatus.Completed or TenantCommandStatus.Faulted &&
            request.ChangeWasMadeDespiteError is not null &&
            await request.ChangeWasMadeDespiteError(result, cancellationToken))
        {
            success = true;
            note = "The script reported an error, but a check of the tenant shows the change was made: " +
                   (result.ErrorMessage ?? "unknown error");
        }

        if (success)
        {
            request.OnSuccess?.Invoke(result);
            if (request.OnSuccessAsync is not null)
            {
                await request.OnSuccessAsync(result, cancellationToken);
            }
        }
        else
        {
            request.OnFailure?.Invoke(result);
            if (request.OnFailureAsync is not null)
            {
                await request.OnFailureAsync(result, cancellationToken);
            }
        }

        return new
        {
            status = success ? "applied" : "failed",
            success,
            operation = request.Operation,
            target = request.Target,
            note,
            error = success ? null : result.ErrorMessage,
            errorCategory = success ? (OperationErrorCategory?)null : result.Category,
            retryHint = success ? null : RetryHint(result.ErrorMessage + "\n" + result.RawOutput),
            output = McpJson.Truncate(result.RawOutput),
            backupFile = backup?.File ?? scopedBackup?.File,
            fullBackup = backup is not null ? "written" : fullBackupError is not null ? "failed: " + fullBackupError : "switched off",
            scopedBackup,
            backupNote = backup is not null
                ? null
                : scopedBackup is not null
                    ? "The full tenant backup could not be taken. A backup of the auto attendants and schedules this change touches was written instead; it does not hold the rest of the tenant."
                    : "The automatic backup is switched off."
        };
    }

    /// <summary>
    /// A new resource account is not visible to all Microsoft services at once, and its license
    /// takes time to become active. The errors that follow from that do not say so.
    /// </summary>
    internal static string? RetryHint(string? text)
    {
        string[] delayErrors =
        [
            "does not exist or one of its queried reference-property objects are not present",
            "userId was not found",
            "is not found",
            "lacks appropriate licenses"
        ];
        return text is not null && delayErrors.Any(error => text.Contains(error, StringComparison.OrdinalIgnoreCase))
            ? "When the resource account was created or licensed in the last minutes, this error is a delay in Microsoft's services. " +
              "Wait 30 to 60 seconds, get a new plan and apply it again. It can take several tries."
            : null;
    }

    private object Plan(ChangeRequest request, string fingerprint, string? stateFingerprint)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var expires = _time.GetUtcNow() + PlanLifetime;
        lock (_gate)
        {
            foreach (var expired in _plans.Where(plan => plan.Value.ExpiresUtc <= _time.GetUtcNow()).Select(plan => plan.Key).ToList())
            {
                _plans.Remove(expired);
            }

            _plans[id] = new StoredPlan(request.Operation, fingerprint, stateFingerprint, expires);
        }

        return new
        {
            status = "planned",
            planId = id,
            operation = request.Operation,
            target = request.Target,
            summary = request.Summary,
            script = request.Script,
            expiresUtc = expires,
            next = "Nothing has changed yet. Show this plan to the user. To run it, call the same tool again " +
                   "with the same arguments and this planId. A full tenant backup is taken before the first change of a session."
        };
    }

    private void TakePlan(string planId, string operation, string fingerprint, string? stateFingerprint)
    {
        lock (_gate)
        {
            if (!_plans.Remove(planId.Trim(), out var plan))
            {
                throw new InvalidOperationException(
                    "This planId is unknown or was already used. Call the tool without planId to get a new plan.");
            }

            if (plan.ExpiresUtc <= _time.GetUtcNow())
            {
                throw new InvalidOperationException("This plan has expired. Call the tool without planId to get a new plan.");
            }

            // First the tenant: a script that holds values read from the tenant changes with it too.
            if (string.Equals(plan.Operation, operation, StringComparison.Ordinal) &&
                !string.Equals(plan.StateFingerprint, stateFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The tenant changed after this plan was made, or the arguments are not the same: what the plan showed is not what the change would save now. " +
                    "Nothing was changed. Call the tool without planId to review the new plan.");
            }

            if (!string.Equals(plan.Operation, operation, StringComparison.Ordinal) ||
                !string.Equals(plan.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The arguments or the configuration changed after this plan was made, so the script is different. " +
                    "Call the tool without planId to review the new plan.");
            }
        }
    }

    private sealed record StoredPlan(string Operation, string Fingerprint, string? StateFingerprint, DateTimeOffset ExpiresUtc);
}
