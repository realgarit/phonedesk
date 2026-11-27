using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// The recorded Guided Setup and bulk runs, and the rollback of one. The desktop app has the same
/// list and the same rollback on its Setup Runs page; both use <see cref="RunJournal"/>.
/// </summary>
[McpServerToolType]
internal sealed class RunTools
{
    private readonly McpRunJournal _runs;
    private readonly TenantGateway _tenant;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;

    private readonly GuidedSetupProgress _progress;

    public RunTools(McpRunJournal runs, TenantGateway tenant, ChangeGate changes, CreatedObjectGuard guard, GuidedSetupProgress progress)
    {
        _progress = progress;
        _runs = runs;
        _tenant = tenant;
        _changes = changes;
        _guard = guard;
    }

    [McpServerTool(Name = "list_setup_runs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the Guided Setup and bulk runs this server recorded on this computer: what each run was for, its state (running means it was interrupted, failed, completed, rolledBack), the step it failed at, and the objects it created. An object is recorded only when the output of the run has its own 'created' line, so an object the run reused is never in the list. Use it after a failure, and at the start of a session to find a run that did not finish: continue it with guided_setup_resume, or remove what it created with rollback_setup_run. Reads this computer only.")]
    public string List(
        [Description("True to list only runs that neither completed nor were rolled back.")] bool onlyOpen = false)
        => McpJson.Run(() =>
        {
            var tenantId = _tenant.TenantId;
            var runs = _runs.Journal.List()
                .Where(record => !onlyOpen || record.IsOpen)
                .Where(record => tenantId.Length == 0 || string.Equals(record.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                .Select(View)
                .ToList();
            return new
            {
                tenant = tenantId.Length == 0 ? null : tenantId,
                note = tenantId.Length == 0 ? "Not connected: the runs of all tenants are listed." : null,
                currentRunId = _runs.Current?.Id,
                runs
            };
        });

    [McpServerTool(Name = "rollback_setup_run", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Removes the objects one recorded run created, in reverse order: auto attendant, call queue, resource accounts, then the agent group. A phone number is never removed from the tenant: removing the resource account takes the number off it, and the number stays in the number inventory (for a Direct Routing number this server added, use release_phone_number afterwards). DELETES OBJECTS IN THE TENANT. It removes only objects the run created, and each one only while the tenant shows exactly one object with its name and that object has the ID the run recorded; every other object stays and the result says why. Objects that existed before the run are never removed. Call it first without planId: it reads the tenant, returns what would be removed and what stays, and changes nothing. Show that to the user and get their agreement. Then call it again with the planId; a tenant that changed in between refuses the plan. Deleted users and groups stay restorable in Microsoft Entra for 30 days. A run of the desktop app is not in this list; roll it back there.")]
    public Task<string> Rollback(
        [Description("The id of the run, from list_setup_runs.")] string runId,
        [Description("The planId from the first call. Omit to get the plan.")] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            _tenant.RequireReady();
            var record = _runs.Journal.Find(runId ?? string.Empty)
                ?? throw new ArgumentException($"'{runId}' is not a recorded run. Use list_setup_runs.");
            if (!string.Equals(record.TenantId, _tenant.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The run '{record.Label}' belongs to another tenant ({record.TenantName} {record.TenantId}). Connect to that tenant to roll it back.");
            }

            var plan = await _runs.Journal.PlanRollbackAsync(record, script => _runs.ReadAsync(script, cancellationToken));
            var items = plan.Items.Select(item => new
            {
                type = item.Object.Type,
                name = item.Object.Name,
                action = item.Action,
                reason = item.Reason
            }).ToList();
            if (!plan.HasWork)
            {
                // Nothing to delete. What the check found gone is written to the record.
                var closed = _runs.Journal.FinishRollback(record, plan, string.Empty);
                Forget(closed);
                EndActiveRun(record);
                return new { success = true, applied = false, nothingToRemove = true, run = View(record), items };
            }

            IReadOnlyList<RollbackOutcome>? outcomes = null;
            var result = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "RollbackSetupRun",
                    record.Label,
                    _runs.Journal.GetRollbackCommand(plan),
                    $"Roll back the run '{record.Label}': remove " +
                    string.Join(", ", plan.Items.Where(item => item.Action == RollbackAction.Delete).Select(item => $"{item.Object.Type} {item.Object.Name}")) + ".")
                {
                    ApprovedState = RunJournal.ApprovedState(plan),
                    // The script reports per object. The rollback succeeded when every object it was to remove is gone.
                    IsSuccess = scriptResult =>
                    {
                        outcomes = _runs.Journal.FinishRollback(
                            record, plan, string.IsNullOrEmpty(scriptResult.RawOutput) ? scriptResult.Value : scriptResult.RawOutput);
                        Forget(outcomes);
                        EndActiveRun(record);
                        return outcomes.All(outcome => outcome.Removed || plan.Items.First(item => ReferenceEquals(item.Object, outcome.Object)).Action != RollbackAction.Delete);
                    }
                },
                planId,
                cancellationToken);

            result = McpJson.With(result, "items", items);
            return outcomes is null
                ? result
                : McpJson.With(
                    McpJson.With(result, "outcome", outcomes.Select(outcome => new
                    {
                        type = outcome.Object.Type,
                        name = outcome.Object.Name,
                        removed = outcome.Removed,
                        message = outcome.Message
                    })),
                    "run", View(record));
        });

    /// <summary>
    /// A rollback of the run in progress ends that run in this session: its steps are open again,
    /// and the next step starts a new record. Otherwise the session would write its older copy of
    /// the record over the result of the rollback.
    /// </summary>
    private void EndActiveRun(RunRecord rolledBack)
    {
        if (string.Equals(_runs.Current?.Id, rolledBack.Id, StringComparison.OrdinalIgnoreCase))
        {
            _runs.Reset();
            _progress.Reset();
        }
    }

    /// <summary>An object a rollback removed is no longer one the server created.</summary>
    private void Forget(IEnumerable<RollbackOutcome> outcomes)
    {
        foreach (var outcome in outcomes.Where(item => item.Removed))
        {
            var (type, key) = outcome.Object.Type switch
            {
                RunObjectType.M365Group => (TenantObjectType.M365Group, outcome.Object.ObjectId),
                RunObjectType.ResourceAccount => (TenantObjectType.ResourceAccount, outcome.Object.Name),
                RunObjectType.CallQueue => (TenantObjectType.CallQueue, outcome.Object.Name),
                _ => (TenantObjectType.AutoAttendant, outcome.Object.Name)
            };
            if (key.Length > 0)
            {
                _guard.Forget(type, key);
            }
        }
    }

    internal static object View(RunRecord record) => new
    {
        id = record.Id,
        kind = record.Kind,
        head = record.Head,
        label = record.Label,
        tenant = record.TenantName.Length == 0 ? record.TenantId : record.TenantName,
        state = record.State,
        startedUtc = record.StartedUtc,
        updatedUtc = record.UpdatedUtc,
        failedStep = record.FailedStep,
        error = record.Error.Length == 0 ? null : record.Error,
        completedSteps = record.CompletedSteps.Keys.Order().ToList(),
        canRollBack = record.CanRollBack,
        objects = record.Objects.Select(item => new
        {
            type = item.Type,
            name = item.Name,
            step = item.Step,
            hasObjectId = item.ObjectId.Length > 0,
            removed = item.Removed,
            rollbackNote = item.RollbackNote.Length == 0 ? null : item.RollbackNote
        })
    };
}
