using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

/// <summary>
/// Keeps the run records for a head: starts a record, writes what each step did, and prepares and
/// closes a rollback. The desktop app and the MCP server both work through this class, each with
/// its own store directory. Tenant reads are passed in, because each head runs commands its own way.
/// </summary>
public sealed class RunJournalService
{
    private readonly IRunJournalStore _store;
    private readonly IPowerShellCommandService _commands;
    private readonly TimeProvider _time;

    public RunJournalService(IRunJournalStore store, IPowerShellCommandService commands, TimeProvider? time = null)
    {
        _store = store;
        _commands = commands;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public IReadOnlyList<RunRecord> List() => _store.List();

    public RunRecord? Find(string id) => _store.Find(id);

    /// <summary>The runs of a tenant that neither finished nor were rolled back, newest first.</summary>
    public IReadOnlyList<RunRecord> Open(string? tenantId, RunKind? kind = null)
        => _store.List()
            .Where(record => record.IsOpen && (kind is null || record.Kind == kind) &&
                             string.Equals(record.TenantId, tenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public RunRecord Begin(RunKind kind, string head, string? tenantId, string? tenantName, string label)
    {
        var record = RunJournal.Start(kind, head, tenantId, tenantName, label, Now);
        _store.Save(record);
        return record;
    }

    /// <summary>Writes a Guided Setup step that completed, with the object it created when its output proves that.</summary>
    public void StepSucceeded(RunRecord record, GuidedSetupStep step, IPhoneManagerVariables variables, string fingerprint, string? output)
    {
        RecordObject(record, step, variables, output);
        record.CompletedSteps[(int)step] = fingerprint;
        record.State = RunState.Running;
        record.FailedStep = null;
        record.Error = string.Empty;
        Save(record);
    }

    /// <summary>
    /// Writes a Guided Setup step that failed. A script can create its object and fail afterwards,
    /// so the object is recorded when its own "created" line is in the output.
    /// </summary>
    public void StepFailed(RunRecord record, GuidedSetupStep step, IPhoneManagerVariables variables, string? error, string? output)
    {
        RecordObject(record, step, variables, output);
        record.CompletedSteps.Remove((int)step);
        record.State = RunState.Failed;
        record.FailedStep = (int)step;
        record.Error = error ?? string.Empty;
        Save(record);
    }

    /// <summary>Writes the result of a bulk run: for every row the objects with their own "created" line.</summary>
    public void BulkFinished(RunRecord record, IReadOnlyList<IPhoneManagerVariables> rows, string? output, bool succeeded, string? error)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            foreach (var (type, name) in RunJournal.ObjectsOfRow(rows[index]))
            {
                RunJournal.Record(record, type, name, index + 1, output, Now, EffectiveName);
            }
        }

        record.State = succeeded ? RunState.Completed : RunState.Failed;
        record.Error = succeeded ? string.Empty : error ?? string.Empty;
        Save(record);
    }

    public void Complete(RunRecord record)
    {
        record.State = RunState.Completed;
        record.FailedStep = null;
        record.Error = string.Empty;
        Save(record);
    }

    public void Save(RunRecord record)
    {
        record.UpdatedUtc = Now;
        _store.Save(record);
    }

    /// <summary>
    /// Reads the IDs of the objects that have none yet. A new object is not visible in the tenant
    /// at once, so this is called after every step of the session that created it. A read that
    /// fails changes nothing.
    /// </summary>
    public async Task StoreIdsAsync(RunRecord record, Func<string, Task<string?>> read)
    {
        var missing = record.Objects.Where(item => !item.Removed && string.IsNullOrWhiteSpace(item.ObjectId)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            var identities = RunJournal.ParseIdentity(await read(_commands.GetRunIdentityCommand(missing)));
            if (RunJournal.StoreIds(missing, identities))
            {
                Save(record);
            }
        }
        catch (InvalidDataException)
        {
            // The tenant could not be read now. The objects stay without an ID and a rollback will not remove them.
        }
    }

    /// <summary>Reads the tenant and decides for each object of the run whether a rollback removes it.</summary>
    public async Task<RollbackPlan> PlanRollbackAsync(RunRecord record, Func<string, Task<string?>> read)
    {
        var ordered = RunJournal.RollbackOrder(record);
        if (ordered.Count == 0)
        {
            return new RollbackPlan();
        }

        var identities = RunJournal.ParseIdentity(await read(_commands.GetRunIdentityCommand(ordered)));
        return RunJournal.Assess(ordered, identities);
    }

    public string GetRollbackCommand(RollbackPlan plan) => RunJournal.GetRollbackCommand(plan, _commands);

    /// <summary>Writes the result of a rollback script to the record and closes the record when nothing is left.</summary>
    public IReadOnlyList<RollbackOutcome> FinishRollback(RunRecord record, RollbackPlan plan, string? output)
    {
        var outcomes = RunJournal.ApplyOutcome(plan, output, Now);
        RunJournal.CloseAfterRollback(record, Now);
        _store.Save(record);
        return outcomes;
    }

    /// <summary>The name as the scripts print it. A head without the rule (a test double) gives the name itself.</summary>
    private string EffectiveName(string name) => _commands.GetEffectiveObjectName(name) ?? name;

    /// <summary>True when the record belongs to this tenant. A run never continues in another tenant.</summary>
    public static bool IsOfTenant(RunRecord record, string? tenantId)
        => !string.IsNullOrWhiteSpace(record.TenantId) &&
           string.Equals(record.TenantId, tenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void RecordObject(RunRecord record, GuidedSetupStep step, IPhoneManagerVariables variables, string? output)
    {
        if (RunJournal.ObjectOfStep(step, variables) is { } created)
        {
            RunJournal.Record(record, created.Type, created.Name, (int)step, output, Now, EffectiveName);
        }
    }
}
