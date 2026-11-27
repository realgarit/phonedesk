using PhoneDesk.Operations;

namespace PhoneDesk.Services;

/// <summary>
/// Keeps the profiles in one JSON file. The file is written to a temporary name first and then
/// moved, so a crash while writing leaves the earlier file. A file that cannot be read gives an
/// empty set and is left in place, so nothing is overwritten by accident.
/// </summary>
public sealed class FileTenantProfileStore : ITenantProfileStore
{
    private readonly string _file;
    private bool _unreadable;

    /// <summary>The store of the desktop app: profiles.json in the local application data of the user.</summary>
    public FileTenantProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneDesk", "profiles.json"))
    {
    }

    public FileTenantProfileStore(string file)
    {
        _file = file;
    }

    public TenantProfileSet Load()
    {
        if (!File.Exists(_file))
        {
            return new TenantProfileSet();
        }

        try
        {
            _unreadable = false;
            return TenantProfileService.Deserialize(File.ReadAllText(_file));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _unreadable = true;
            return new TenantProfileSet();
        }
    }

    public void Save(TenantProfileSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (_unreadable)
        {
            throw new IOException($"The profile file '{_file}' could not be read, so it is not overwritten. Repair or remove it first.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, TenantProfileService.Serialize(set));
        File.Move(temporary, _file, overwrite: true);
    }
}
