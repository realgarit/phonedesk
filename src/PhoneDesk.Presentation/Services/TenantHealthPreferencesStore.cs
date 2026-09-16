using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneDesk.HealthChecks;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class TenantHealthPreferencesStore : ITenantHealthPreferencesStore
{
    private const int SchemaVersion = 1;
    private readonly string _filePath;
    private readonly object _sync = new();

    public TenantHealthPreferencesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDesk"))
    {
    }

    public TenantHealthPreferencesStore(string settingsDirectory)
    {
        _filePath = Path.Combine(settingsDirectory, "tenant-health-preferences.json");
    }

    public TenantHealthPreferences Load(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return TenantHealthPreferences.Default;
        }

        lock (_sync)
        {
            if (!TryRead(out var document) || document is null ||
                !document.Tenants.TryGetValue(tenantId, out var settings))
            {
                return TenantHealthPreferences.Default;
            }

            return new TenantHealthPreferences(
                settings.DisabledRuleIds.ToHashSet(StringComparer.Ordinal),
                settings.SuppressedFindingIds.ToHashSet(StringComparer.Ordinal));
        }
    }

    public bool Save(string tenantId, TenantHealthPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        lock (_sync)
        {
            PreferencesDocument? document = null;
            if (File.Exists(_filePath) && !TryRead(out document))
            {
                return false;
            }
            document ??= new PreferencesDocument();
            document.Tenants[tenantId] = new TenantSettings
            {
                DisabledRuleIds = preferences.DisabledRuleIds.Order(StringComparer.Ordinal).ToArray(),
                SuppressedFindingIds = preferences.SuppressedFindingIds.Order(StringComparer.Ordinal).ToArray(),
            };
            return TryWrite(document);
        }
    }

    private bool TryRead(out PreferencesDocument? document)
    {
        document = null;
        try
        {
            if (!File.Exists(_filePath))
            {
                document = new PreferencesDocument();
                return true;
            }
            using var stream = File.OpenRead(_filePath);
            document = JsonSerializer.Deserialize<PreferencesDocument>(stream);
            return document is not null && document.SchemaVersion == SchemaVersion && document.Tenants is not null;
        }
        catch
        {
            return false;
        }
    }

    private bool TryWrite(PreferencesDocument document)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class PreferencesDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = TenantHealthPreferencesStore.SchemaVersion;

        [JsonPropertyName("tenants")]
        public Dictionary<string, TenantSettings> Tenants { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class TenantSettings
    {
        [JsonPropertyName("disabledRuleIds")]
        public IReadOnlyList<string> DisabledRuleIds { get; init; } = Array.Empty<string>();

        [JsonPropertyName("suppressedFindingIds")]
        public IReadOnlyList<string> SuppressedFindingIds { get; init; } = Array.Empty<string>();
    }
}
