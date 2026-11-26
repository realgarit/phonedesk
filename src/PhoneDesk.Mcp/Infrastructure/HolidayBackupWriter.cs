using PhoneDesk.Operations;
using PhoneDesk.Services;

namespace PhoneDesk.Mcp;

/// <summary>A scoped backup that was written: the file and what it holds.</summary>
internal sealed record ScopedBackupInfo(string File, int AutoAttendants, int Schedules, int CallFlows, int Links);

/// <summary>
/// Writes a backup of the holiday call handling of some auto attendants and schedules. A holiday
/// change uses it when the full tenant backup cannot be taken (one query that warns stops the full
/// snapshot): the change may run when everything it touches is in this backup, and not otherwise.
/// </summary>
internal sealed class HolidayBackupWriter
{
    private readonly HolidayCallHandlingReader _reader;
    private readonly TenantGateway _tenant;
    private readonly McpEnvironment _environment;
    private readonly TimeProvider _time;

    public HolidayBackupWriter(HolidayCallHandlingReader reader, TenantGateway tenant, McpEnvironment environment, TimeProvider time)
    {
        _reader = reader;
        _tenant = tenant;
        _environment = environment;
        _time = time;
    }

    /// <summary>
    /// Reads the given auto attendants (all when none is given) and the schedules, and writes the
    /// backup. Throws when an object that was asked for is not in what the tenant returned.
    /// </summary>
    public async Task<ScopedBackupInfo> WriteAsync(
        IReadOnlyCollection<string> autoAttendantIds,
        IReadOnlyCollection<string> scheduleIds,
        CancellationToken cancellationToken)
    {
        var ids = autoAttendantIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var state = await _reader.ReadAsync(ids.Length == 0 ? null : ids, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
        var result = HolidayBackup.Create(
            state,
            ids.Length == 0 ? state.AutoAttendants.Select(attendant => attendant.Id) : ids,
            scheduleIds,
            _time.GetUtcNow());
        if (!result.IsComplete)
        {
            throw new InvalidDataException("The backup is not complete; the tenant did not return " + result.MissingText + ".");
        }

        var file = new FileHolidayBackupStore(_environment.BackupDirectory(_tenant.TenantId)).Write(result.Document);
        return new ScopedBackupInfo(file, result.Document.AutoAttendants.Count, result.Document.Schedules.Count, result.CallFlows, result.Links);
    }

    /// <summary>The scoped backup of a change, for <see cref="ChangeRequest.ScopedBackupAsync"/>.</summary>
    public Func<CancellationToken, Task<ScopedBackupInfo>> For(IEnumerable<string?> autoAttendantIds, IEnumerable<string?> scheduleIds)
    {
        var attendants = autoAttendantIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
        var schedules = scheduleIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
        return token => WriteAsync(attendants, schedules, token);
    }
}
