using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp;

/// <summary>
/// The run record of the server session: one record for the Guided Setup in progress, and one per
/// bulk run. It uses the same journal as the desktop app, with the store in the data directory of
/// the server. A journal that cannot be written never fails a change.
/// </summary>
internal sealed class McpRunJournal
{
    private readonly RunJournalService _journal;
    private readonly TenantGateway _tenant;
    private readonly ISessionManager _session;

    public McpRunJournal(RunJournalService journal, TenantGateway tenant, ISessionManager session)
    {
        _journal = journal;
        _tenant = tenant;
        _session = session;
    }

    public RunJournalService Journal => _journal;

    /// <summary>The Guided Setup run of this session, or null before its first step ran.</summary>
    public RunRecord? Current { get; private set; }

    public void Adopt(RunRecord record) => Current = record;

    /// <summary>Forgets the run in progress. Its record stays, so what it created can still be rolled back.</summary>
    public void Reset() => Current = null;

    public async Task StepAsync(
        GuidedSetupStep step, bool succeeded, IPhoneManagerVariables variables, string fingerprint, string? output, string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            // After a change of tenant the run of the other tenant is not continued: its record stays as it is.
            if (Current is { } active && !RunJournalService.IsOfTenant(active, _tenant.TenantId))
            {
                Current = null;
            }

            var run = Current ??= _journal.Begin(
                RunKind.GuidedSetup, "mcp", _tenant.TenantId, _session.TenantName, variables.Customer + " - " + variables.CustomerGroupName);
            if (succeeded)
            {
                _journal.StepSucceeded(run, step, variables, fingerprint, output);
            }
            else
            {
                _journal.StepFailed(run, step, variables, error, output);
            }

            await _journal.StoreIdsAsync(run, script => ReadAsync(script, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The step result stands; only the record of it is missing.
        }
    }

    public void Complete()
    {
        if (Current is { } run)
        {
            try
            {
                _journal.Complete(run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing to do: the tenant is as the steps left it.
            }

            Current = null;
        }
    }

    public async Task BulkAsync(IReadOnlyList<IPhoneManagerVariables> rows, string? output, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        try
        {
            var label = string.Join(", ", rows.Select(row => row.Customer + " - " + row.CustomerGroupName).Take(3)) + (rows.Count > 3 ? ", ..." : string.Empty);
            var run = _journal.Begin(RunKind.Bulk, "mcp", _tenant.TenantId, _session.TenantName, label);
            _journal.BulkFinished(run, rows, output, succeeded, error);
            await _journal.StoreIdsAsync(run, script => ReadAsync(script, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The bulk result stands; only the record of it is missing.
        }
    }

    public async Task<string?> ReadAsync(string script, CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(script, "ReadRunObjects", cancellationToken);
        return string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput;
    }
}
