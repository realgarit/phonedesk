using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>
/// A named customer: the tenant the operator expects to be connected to, the account they sign in
/// with, and the configuration of that customer. A profile holds no password and no token.
/// </summary>
public sealed class TenantProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The tenant ID, a verified domain or the name of the tenant. Empty when it is not checked.</summary>
    public string ExpectedTenant { get; set; } = string.Empty;

    /// <summary>The sign-in name the operator uses for this customer. A hint and a check, never a credential.</summary>
    public string ExpectedAccount { get; set; } = string.Empty;

    /// <summary>The configuration of this customer, as the configuration file format.</summary>
    public string ConfigurationJson { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

public sealed class TenantProfileSet
{
    public string ActiveId { get; set; } = string.Empty;

    public List<TenantProfile> Profiles { get; set; } = new();
}

public interface ITenantProfileStore
{
    TenantProfileSet Load();

    void Save(TenantProfileSet set);
}

/// <summary>
/// The profiles of a head. A switch gives the caller the profile to load; the caller then puts
/// its configuration in place, sets the expected tenant, and signs out of the connections, so the
/// next sign-in is checked against the new profile. Both heads work through this class.
/// </summary>
public sealed class TenantProfileService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly ITenantProfileStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public TenantProfileService(ITenantProfileStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public static string Serialize(TenantProfileSet set) => JsonSerializer.Serialize(set, Options);

    public static TenantProfileSet Deserialize(string json) => JsonSerializer.Deserialize<TenantProfileSet>(json, Options) ?? new TenantProfileSet();

    public IReadOnlyList<TenantProfile> List()
    {
        lock (_gate)
        {
            return _store.Load().Profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The profile in use, or null when none was chosen.</summary>
    public TenantProfile? Active
    {
        get
        {
            lock (_gate)
            {
                var set = _store.Load();
                return set.Profiles.FirstOrDefault(profile => profile.Id == set.ActiveId);
            }
        }
    }

    public TenantProfile? Find(string? nameOrId)
    {
        var text = (nameOrId ?? string.Empty).Trim();
        return List().FirstOrDefault(profile =>
            string.Equals(profile.Id, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a profile with the configuration that is in place now. It does not become the active one.</summary>
    public TenantProfile Create(string name, string? expectedTenant, string? expectedAccount, string configurationJson)
    {
        lock (_gate)
        {
            var set = _store.Load();
            var clean = RequireName(set, name, null);
            var profile = new TenantProfile
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = clean,
                ExpectedTenant = (expectedTenant ?? string.Empty).Trim(),
                ExpectedAccount = (expectedAccount ?? string.Empty).Trim(),
                ConfigurationJson = configurationJson ?? string.Empty,
                UpdatedUtc = _time.GetUtcNow().UtcDateTime
            };
            set.Profiles.Add(profile);
            _store.Save(set);
            return profile;
        }
    }

    /// <summary>Changes the name, the expected tenant and the account of a profile. Its configuration stays.</summary>
    public TenantProfile Update(string id, string name, string? expectedTenant, string? expectedAccount)
    {
        lock (_gate)
        {
            var set = _store.Load();
            var profile = Require(set, id);
            profile.Name = RequireName(set, name, profile.Id);
            profile.ExpectedTenant = (expectedTenant ?? string.Empty).Trim();
            profile.ExpectedAccount = (expectedAccount ?? string.Empty).Trim();
            profile.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
            _store.Save(set);
            return profile;
        }
    }

    /// <summary>Writes the configuration that is in place now into a profile.</summary>
    public void SaveConfiguration(string id, string configurationJson)
    {
        lock (_gate)
        {
            var set = _store.Load();
            var profile = Require(set, id);
            profile.ConfigurationJson = configurationJson ?? string.Empty;
            profile.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
            _store.Save(set);
        }
    }

    /// <summary>
    /// Makes a profile the active one. The configuration that is in place now is first written
    /// into the profile that was active, so nothing an operator entered is lost by a switch.
    /// </summary>
    public TenantProfile Activate(string id, string currentConfigurationJson)
    {
        lock (_gate)
        {
            var set = _store.Load();
            var next = Require(set, id);
            var previous = set.Profiles.FirstOrDefault(profile => profile.Id == set.ActiveId);
            if (previous is not null && previous.Id != next.Id)
            {
                previous.ConfigurationJson = currentConfigurationJson ?? string.Empty;
                previous.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
            }

            set.ActiveId = next.Id;
            _store.Save(set);
            return next;
        }
    }

    /// <summary>Removes a profile. The tenant of that customer is not touched.</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            var set = _store.Load();
            var profile = Require(set, id);
            set.Profiles.Remove(profile);
            if (set.ActiveId == profile.Id)
            {
                set.ActiveId = string.Empty;
            }

            _store.Save(set);
        }
    }

    private static TenantProfile Require(TenantProfileSet set, string id)
        => set.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, (id ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException("The profile does not exist.");

    private static string RequireName(TenantProfileSet set, string? name, string? ownId)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("A profile needs a name.");
        }

        if (clean.Length > 80)
        {
            throw new ArgumentException("The name of a profile has 80 characters at most.");
        }

        if (set.Profiles.Any(profile => profile.Id != ownId && string.Equals(profile.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A profile named '{clean}' exists already.");
        }

        return clean;
    }
}
