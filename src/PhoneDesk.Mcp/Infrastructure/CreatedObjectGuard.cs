using System.Text.RegularExpressions;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp;

/// <summary>
/// Decides which objects the server may call its own, and which objects a delete tool may remove.
/// The delete scripts find an object by name and take the first match, so the guard never relies
/// on a name alone: an object is claimed only when the backup taken before the session's first
/// change proves it did not exist, and a name-based delete runs only when the name is unique in
/// the live tenant.
/// </summary>
internal sealed class CreatedObjectGuard
{
    private readonly TenantGateway _tenant;
    private readonly CreatedObjectLedger _ledger;
    private readonly McpEnvironment _environment;
    private readonly IDocumentationScriptBuilder _documentationScripts;

    public CreatedObjectGuard(
        TenantGateway tenant,
        CreatedObjectLedger ledger,
        McpEnvironment environment,
        IDocumentationScriptBuilder documentationScripts)
    {
        _tenant = tenant;
        _ledger = ledger;
        _environment = environment;
        _documentationScripts = documentationScripts;
    }

    /// <summary>True when the script reported that it used an object that was already in the tenant.</summary>
    public static bool ReusedExistingObject(string? output)
        => output?.Contains("already exist", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Records an object as created by this server after a successful create script. Nothing is
    /// recorded when the script reused an existing object, when there is no pre-change backup to
    /// prove the object is new, or when the backup already contains it.
    /// </summary>
    public void Claim(TenantObjectType type, string name, string operation, string? output)
    {
        if (ReusedExistingObject(output) || _tenant.BaselineSnapshot is not { } baseline || ExistedIn(baseline, type, name))
        {
            return;
        }

        if (type == TenantObjectType.M365Group)
        {
            // A group is deleted by its object ID, so the ID is what the ledger must hold. The ID is
            // taken from this group's own line, so a script that created several groups is safe.
            var match = Regex.Match(
                output ?? string.Empty,
                Regex.Escape(name.Trim()) + @" created successfully with ID:\s*(?<id>[0-9a-fA-F-]{36})",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                _ledger.Record(_tenant.TenantId, type, match.Groups["id"].Value, operation, name);
            }

            return;
        }

        _ledger.Record(_tenant.TenantId, type, name, operation, name);
    }

    /// <summary>
    /// True when a script failed only at the very end: the Teams module can throw a null reference
    /// after the script finished its work, and the host then returns that error instead of the
    /// script's output. The object is read from the tenant again to confirm it is there.
    /// </summary>
    public async Task<bool> ExistsAfterLateModuleErrorAsync(
        TenantObjectType type, string key, Services.OperationResult<string> result, CancellationToken cancellationToken)
        => (result.ErrorMessage ?? result.RawOutput).Contains(
               "Object reference not set to an instance of an object", StringComparison.OrdinalIgnoreCase) &&
           await ExistsAsync(type, key, cancellationToken);

    /// <summary>
    /// Records an object after a create script that reported an error. A failed script proves
    /// nothing by itself, so the object is recorded only when a new read of the tenant shows it
    /// exists (and, as always, the pre-change backup shows it did not exist before). A group
    /// needs its own "created" line with the object ID in the output.
    /// </summary>
    public async Task ClaimAfterFailureAsync(
        TenantObjectType type, string name, string operation, Services.OperationResult<string> result, CancellationToken cancellationToken)
    {
        var text = result.RawOutput + "\n" + result.ErrorMessage;
        if (ReusedExistingObject(text))
        {
            return;
        }

        try
        {
            if (type == TenantObjectType.M365Group || await ExistsAsync(type, name, cancellationToken))
            {
                Claim(type, name, operation, text);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // The tenant could not be read: nothing is recorded.
        }
    }

    public void Forget(TenantObjectType type, string key) => _ledger.Remove(_tenant.TenantId, type, key);

    /// <summary>Throws when a delete tool must not remove the object.</summary>
    /// <param name="key">The value the delete script identifies the object by: a name, a UPN, or a group object ID.</param>
    public async Task RequireDeletableAsync(TenantObjectType type, string key, CancellationToken cancellationToken)
    {
        _tenant.RequireReady();
        if (!_environment.AllowDeleteExisting && !_ledger.Contains(_tenant.TenantId, type, key))
        {
            throw new InvalidOperationException(
                $"'{key}' was not created by this server, so it is not deleted. Objects this server created are listed by " +
                $"created_objects. To allow deleting other objects, the operator must start the server with " +
                $"{McpEnvironment.AllowDeleteExistingVariable}=1, or delete the object in the desktop app or the Teams admin center.");
        }

        var matches = await CountInTenantAsync(type, key, cancellationToken);
        if (matches == 0)
        {
            // The object is gone, for example removed together with its auto attendant: nothing is left to track.
            _ledger.Remove(_tenant.TenantId, type, key);
            throw new InvalidOperationException($"'{key}' was not found in the tenant. There is nothing to delete.");
        }

        if (matches > 1)
        {
            throw new InvalidOperationException(
                $"The tenant has {matches} objects named '{key}'. The delete script cannot tell them apart, so nothing is deleted. " +
                "Delete the right one in the Teams admin center.");
        }
    }

    /// <summary>True when the live tenant still has the object. Reads the tenant again.</summary>
    public async Task<bool> ExistsAsync(TenantObjectType type, string key, CancellationToken cancellationToken)
        => await CountInTenantAsync(type, key, cancellationToken) > 0;

    private async Task<int> CountInTenantAsync(TenantObjectType type, string key, CancellationToken cancellationToken)
    {
        if (type == TenantObjectType.HolidaySchedule)
        {
            return await CountSchedulesAsync(key, cancellationToken);
        }

        var topology = await _tenant.GetTopologyAsync(refresh: true, cancellationToken);
        return type switch
        {
            TenantObjectType.CallQueue => topology.CallQueues.Count(item => SameName(item.Name, key)),
            TenantObjectType.AutoAttendant => topology.AutoAttendants.Count(item => SameName(item.Name, key)),
            TenantObjectType.ResourceAccount => topology.ResourceAccounts.Count(item => SameName(item.UserPrincipalName, key)),
            TenantObjectType.M365Group => topology.Groups.Count(item => SameName(item.Id, key)),
            _ => 0
        };
    }

    private async Task<int> CountSchedulesAsync(string name, CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(_documentationScripts.GetExportSchedulesCommand(), "ReadSchedules", cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                "The schedules could not be read, so nothing is deleted: " + (result.ErrorMessage ?? "unknown error"));
        }

        const string marker = "DOCDATA_SCHED:";
        return (result.Value ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Count(line => SameName(line[marker.Length..].Split('|')[0], name));
    }

    private static bool ExistedIn(TopologySnapshotDocument snapshot, TenantObjectType type, string name)
        => type switch
        {
            TenantObjectType.M365Group => snapshot.Groups.Any(item => SameName(item.DisplayName, name)),
            TenantObjectType.ResourceAccount => snapshot.ResourceAccounts.Any(item => SameName(item.UserPrincipalName, name)),
            TenantObjectType.CallQueue => snapshot.CallQueues.Any(item => SameName(item.Name, name)),
            TenantObjectType.AutoAttendant => snapshot.AutoAttendants.Any(item => SameName(item.Name, name)),
            // Without the schedule inventory there is no proof the schedule is new.
            TenantObjectType.HolidaySchedule => snapshot.Documentation?.Schedules.Any(item => SameName(item.Name, name)) ?? true,
            _ => true
        };

    private static bool SameName(string? left, string right)
        => string.Equals(left?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
