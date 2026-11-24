using System.Text.Json;

namespace PhoneDesk.Mcp;

internal enum TenantObjectType
{
    M365Group,
    ResourceAccount,
    CallQueue,
    AutoAttendant,
    HolidaySchedule
}

internal sealed record CreatedObject(TenantObjectType Type, string Key, DateTimeOffset CreatedUtc, string Operation, string? DisplayName = null);

/// <summary>
/// Remembers, for each tenant, the objects this server created. The delete tools accept only
/// these objects, so the server cannot remove something that was in the tenant before it.
/// </summary>
internal sealed class CreatedObjectLedger
{
    private readonly McpEnvironment _environment;
    private readonly Lock _gate = new();

    public CreatedObjectLedger(McpEnvironment environment)
    {
        _environment = environment;
    }

    public IReadOnlyList<CreatedObject> List(string tenantId)
    {
        lock (_gate)
        {
            return Read().TryGetValue(tenantId, out var objects) ? objects : new List<CreatedObject>();
        }
    }

    public bool Contains(string tenantId, TenantObjectType type, string key)
        => List(tenantId).Any(item => item.Type == type && Matches(item.Key, key));

    public void Record(string tenantId, TenantObjectType type, string key, string operation, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            var all = Read();
            if (!all.TryGetValue(tenantId, out var objects))
            {
                objects = new List<CreatedObject>();
                all[tenantId] = objects;
            }

            if (!objects.Any(item => item.Type == type && Matches(item.Key, key)))
            {
                objects.Add(new CreatedObject(type, key.Trim(), DateTimeOffset.UtcNow, operation, displayName));
                Write(all);
            }
        }
    }

    public void Remove(string tenantId, TenantObjectType type, string key)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.TryGetValue(tenantId, out var objects) &&
                objects.RemoveAll(item => item.Type == type && Matches(item.Key, key)) > 0)
            {
                Write(all);
            }
        }
    }

    private static bool Matches(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, List<CreatedObject>> Read()
    {
        try
        {
            if (File.Exists(_environment.CreatedObjectsFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<CreatedObject>>>(
                           File.ReadAllText(_environment.CreatedObjectsFile), McpJson.Options)
                       ?? new Dictionary<string, List<CreatedObject>>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable ledger means nothing is known to be created here: deletes stay refused.
        }

        return new Dictionary<string, List<CreatedObject>>();
    }

    private void Write(Dictionary<string, List<CreatedObject>> all)
    {
        Directory.CreateDirectory(_environment.DataDirectory);
        var temporary = _environment.CreatedObjectsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, McpJson.Options));
        File.Move(temporary, _environment.CreatedObjectsFile, overwrite: true);
    }
}
