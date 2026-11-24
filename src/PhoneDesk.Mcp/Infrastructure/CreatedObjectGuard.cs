using System.Text.RegularExpressions;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp;

/// <summary>
/// Decides which objects the server may call its own, and which objects a delete tool may remove.
/// The delete scripts find an object by name and take the first match, so the guard never relies
/// on a name alone: an object is claimed only when the backup taken before the session's first
/// change proves it did not exist, and a name-based delete runs only when the name is unique in
/// the live tenant and still belongs to the object the server created. A name can be given to
/// another object later, so the server stores the immutable object ID and compares it before a delete.
/// </summary>
internal sealed class CreatedObjectGuard
{
    private readonly TenantGateway _tenant;
    private readonly CreatedObjectLedger _ledger;
    private readonly McpEnvironment _environment;
    private readonly IDocumentationScriptBuilder _documentationScripts;

    /// <summary>
    /// Objects recorded in this session whose ID could not be read yet (a new object is not visible
    /// at once). Only these may get their ID later; an entry without an ID from an earlier session
    /// cannot be confirmed any more.
    /// </summary>
    private readonly HashSet<(string TenantId, TenantObjectType Type, string Key)> _awaitingObjectId = new();

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
        lock (_awaitingObjectId)
        {
            _awaitingObjectId.Add(Pending(type, name));
        }
    }

    /// <summary>Records a created object and stores its immutable ID when the tenant already shows it.</summary>
    public async Task ClaimAsync(TenantObjectType type, string name, string operation, string? output, CancellationToken cancellationToken)
    {
        Claim(type, name, operation, output);
        if (type == TenantObjectType.M365Group || !_ledger.Contains(_tenant.TenantId, type, name))
        {
            return;
        }

        try
        {
            TryStoreObjectId(type, name, await FindObjectIdsAsync(type, name, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // The tenant could not be read now; the ID is stored at the next check in this session.
        }
    }

    /// <summary>
    /// Builds the check a create tool runs after its script reported an error. The Teams module can
    /// throw a null reference after a script finished its work, and the host then returns that
    /// error instead of the script's output. When the object did not exist before the call and
    /// exists afterwards, the create was made. For an object that existed before, existence proves
    /// nothing about an update, so the failure stands. Returns null when the change is only planned.
    /// </summary>
    public async Task<Func<Services.OperationResult<string>, CancellationToken, Task<bool>>?> CreatedDespiteLateModuleErrorAsync(
        TenantObjectType type, string key, bool applying, CancellationToken cancellationToken)
    {
        if (!applying)
        {
            return null;
        }

        _tenant.RequireReady();
        if (await ExistsAsync(type, key, cancellationToken))
        {
            return null;
        }

        return async (result, token) =>
            (result.ErrorMessage ?? result.RawOutput).Contains(
                "Object reference not set to an instance of an object", StringComparison.OrdinalIgnoreCase) &&
            await ExistsAsync(type, key, token);
    }

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
            if (type == TenantObjectType.M365Group)
            {
                Claim(type, name, operation, text);
                return;
            }

            var ids = await FindObjectIdsAsync(type, name, cancellationToken);
            if (ids.Count > 0)
            {
                Claim(type, name, operation, text);
                TryStoreObjectId(type, name, ids);
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

        var ids = await FindObjectIdsAsync(type, key, cancellationToken);
        if (ids.Count == 0)
        {
            // The object is gone, for example removed together with its auto attendant: nothing is left to track.
            _ledger.Remove(_tenant.TenantId, type, key);
            throw new InvalidOperationException($"'{key}' was not found in the tenant. There is nothing to delete.");
        }

        if (ids.Count > 1)
        {
            throw new InvalidOperationException(
                $"The tenant has {ids.Count} objects named '{key}'. The delete script cannot tell them apart, so nothing is deleted. " +
                "Delete the right one in the Teams admin center.");
        }

        if (_environment.AllowDeleteExisting || type == TenantObjectType.M365Group)
        {
            return;
        }

        // The name is in the list of created objects. Confirm it is still the same object.
        TryStoreObjectId(type, key, ids);
        var recorded = _ledger.Find(_tenant.TenantId, type, key)?.ObjectId;
        if (string.IsNullOrWhiteSpace(recorded))
        {
            throw new InvalidOperationException(
                $"'{key}' was recorded without its object ID, so the server cannot confirm that the object in the tenant is the one it created. " +
                "Nothing is deleted. Delete it in the desktop app or the Teams admin center.");
        }

        if (!SameName(recorded, ids[0]))
        {
            _ledger.Remove(_tenant.TenantId, type, key);
            throw new InvalidOperationException(
                $"The object named '{key}' in the tenant is not the one this server created: it has a different object ID. " +
                "The created object no longer exists. Nothing is deleted.");
        }
    }

    /// <summary>True when the live tenant still has the object. Reads the tenant again.</summary>
    public async Task<bool> ExistsAsync(TenantObjectType type, string key, CancellationToken cancellationToken)
        => (await FindObjectIdsAsync(type, key, cancellationToken)).Count > 0;

    /// <summary>Stores the object ID of an object recorded in this session, when exactly one object has the name.</summary>
    private void TryStoreObjectId(TenantObjectType type, string key, IReadOnlyList<string> ids)
    {
        lock (_awaitingObjectId)
        {
            if (ids.Count == 1 && !string.IsNullOrWhiteSpace(ids[0]) && _awaitingObjectId.Remove(Pending(type, key)))
            {
                _ledger.SetObjectId(_tenant.TenantId, type, key, ids[0]);
            }
        }
    }

    private (string TenantId, TenantObjectType Type, string Key) Pending(TenantObjectType type, string key)
        => (_tenant.TenantId.ToUpperInvariant(), type, key.Trim().ToUpperInvariant());

    /// <summary>The immutable IDs of the live objects the key points to. Reads the tenant again.</summary>
    private async Task<IReadOnlyList<string>> FindObjectIdsAsync(TenantObjectType type, string key, CancellationToken cancellationToken)
    {
        if (type == TenantObjectType.HolidaySchedule)
        {
            return await FindScheduleIdsAsync(key, cancellationToken);
        }

        if (type == TenantObjectType.PhoneNumber)
        {
            // A number is its own immutable ID.
            return (await ReadPhoneNumbersAsync(cancellationToken))
                .Where(item => SameName(item.Number, key))
                .Select(item => item.Number)
                .ToList();
        }

        var topology = await _tenant.GetTopologyAsync(refresh: true, cancellationToken);
        return type switch
        {
            TenantObjectType.CallQueue => topology.CallQueues.Where(item => SameName(item.Name, key)).Select(item => item.Identity).ToList(),
            TenantObjectType.AutoAttendant => topology.AutoAttendants.Where(item => SameName(item.Name, key)).Select(item => item.Identity).ToList(),
            TenantObjectType.ResourceAccount => topology.ResourceAccounts.Where(item => SameName(item.UserPrincipalName, key)).Select(item => item.ObjectId).ToList(),
            TenantObjectType.M365Group => topology.Groups.Where(item => SameName(item.Id, key)).Select(item => item.Id).ToList(),
            _ => Array.Empty<string>()
        };
    }

    private async Task<IReadOnlyList<string>> FindScheduleIdsAsync(string name, CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(_documentationScripts.GetExportSchedulesCommand(), "ReadSchedules", cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                "The schedules could not be read, so nothing is deleted: " + (result.ErrorMessage ?? "unknown error"));
        }

        // Each line is "DOCDATA_SCHED: name|id|type|date count".
        const string marker = "DOCDATA_SCHED:";
        return (result.Value ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..].Split('|'))
            .Where(fields => fields.Length >= 2 && SameName(fields[0], name))
            .Select(fields => fields[1].Trim())
            .ToList();
    }

    /// <summary>
    /// Throws when the number cannot be released: it is not in the tenant's number inventory, or
    /// it is assigned to a user or a resource account. Reads the tenant again.
    /// </summary>
    public async Task RequireUnassignedPhoneNumberAsync(string number, CancellationToken cancellationToken)
    {
        _tenant.RequireReady();
        var rows = (await ReadPhoneNumbersAsync(cancellationToken)).Where(item => SameName(item.Number, number)).ToList();
        if (rows.Count == 0)
        {
            _ledger.Remove(_tenant.TenantId, TenantObjectType.PhoneNumber, number);
            throw new InvalidOperationException($"'{number}' is not in the tenant's phone number inventory. There is nothing to release.");
        }

        if (rows.Any(item => !string.IsNullOrWhiteSpace(item.AssignedTo) ||
                             !string.Equals(item.Status, "Unassigned", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{number}' is assigned (status {rows[0].Status}), so it is not released. Only an unassigned number can be released.");
        }
    }

    private async Task<IReadOnlyList<(string Number, string AssignedTo, string Status)>> ReadPhoneNumbersAsync(CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(_documentationScripts.GetExportPhoneNumbersCommand(), "ReadPhoneNumbers", cancellationToken);
        var text = result.Value ?? string.Empty;
        if (!result.IsSuccess || !text.Contains("DOCDATA_PHONE_END", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The phone numbers could not be read, so nothing is changed: " + (result.ErrorMessage ?? "incomplete output"));
        }

        // Each line is "DOCDATA_PHONE: number|type|assigned to|status|activation|city|capabilities".
        const string marker = "DOCDATA_PHONE:";
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..].Split('|'))
            .Where(fields => fields.Length >= 4)
            .Select(fields => (fields[0].Trim(), fields[2].Trim(), fields[3].Trim()))
            .ToList();
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
            // A number the tenant already had (for example from an operator) is never the server's.
            TenantObjectType.PhoneNumber => snapshot.Documentation?.PhoneNumbers.Any(item => SameName(item.Number, name)) ?? true,
            _ => true
        };

    private static bool SameName(string? left, string right)
        => string.Equals(left?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
