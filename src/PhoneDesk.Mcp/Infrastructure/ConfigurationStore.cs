using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp;

/// <summary>
/// The configuration the provisioning tools work from. It is the same JSON document the desktop
/// app exports. The server saves it after every change, because an AI client starts a new server
/// process for each session.
/// </summary>
internal sealed class ConfigurationStore
{
    private readonly ITenantAsCodeService _tenantAsCode;
    private readonly McpEnvironment _environment;
    private readonly Lock _gate = new();
    private ConfigurationDocument _document;

    public ConfigurationStore(ITenantAsCodeService tenantAsCode, McpEnvironment environment)
    {
        _tenantAsCode = tenantAsCode;
        _environment = environment;
        _document = LoadSaved() ?? CreateDefault();
    }

    public ConfigurationDocument Document
    {
        get
        {
            lock (_gate)
            {
                return _document;
            }
        }
    }

    public ConfigurationDocumentVariables Variables => new(Document);

    public string ToJson() => _tenantAsCode.SerializeConfiguration(Document);

    /// <summary>Replaces the whole configuration with a schema-v1 configuration document.</summary>
    public IReadOnlyList<ConfigurationChange> Replace(string json)
    {
        var imported = _tenantAsCode.DeserializeConfiguration(json);
        return Set(imported);
    }

    /// <summary>Compares a document with the current configuration without changing anything.</summary>
    public IReadOnlyList<ConfigurationChange> Preview(string json)
        => _tenantAsCode.CompareConfigurations(Document, _tenantAsCode.DeserializeConfiguration(json));

    public IReadOnlyList<ConfigurationChange> Reset() => Set(CreateDefault());

    /// <summary>
    /// Changes single values. Each key is a path such as <c>general.customer</c> or
    /// <c>businessHoursTemplate.openingHours1Start</c>; each value is the new JSON value.
    /// </summary>
    public IReadOnlyList<ConfigurationChange> Patch(IReadOnlyDictionary<string, JsonElement> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new ArgumentException("No changes were given.");
        }

        var root = JsonNode.Parse(ToJson())!.AsObject();
        var configuration = root["configuration"]!.AsObject();
        foreach (var (path, value) in changes)
        {
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0 && string.Equals(segments[0], "configuration", StringComparison.OrdinalIgnoreCase))
            {
                segments = segments[1..];
            }

            if (segments.Length == 0)
            {
                throw new ArgumentException($"'{path}' is not a configuration path.");
            }

            var parent = configuration;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                parent = parent[ResolveKey(parent, segments[index], path)] as JsonObject
                    ?? throw new ArgumentException($"'{path}' does not point to a configuration value.");
            }

            parent[ResolveKey(parent, segments[^1], path)] = JsonNode.Parse(value.GetRawText());
        }

        return Replace(root.ToJsonString());
    }

    /// <summary>Replaces the holiday entries and leaves every other value as it is.</summary>
    public IReadOnlyList<ConfigurationChange> SetHolidaySeries(IReadOnlyList<PortableHolidayEntry> series)
    {
        var current = Document;
        var next = current with
        {
            Configuration = current.Configuration with
            {
                HolidayTemplate = current.Configuration.HolidayTemplate with { Series = series }
            }
        };

        // Through the serializer, so the same checks apply as for every other change.
        return Replace(_tenantAsCode.SerializeConfiguration(next));
    }

    private static string ResolveKey(JsonObject parent, string segment, string path)
    {
        foreach (var property in parent)
        {
            if (string.Equals(property.Key, segment, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        throw new ArgumentException(
            $"'{path}' is not a configuration path. Valid names at '{segment}': " +
            string.Join(", ", parent.Select(property => property.Key)) + ".");
    }

    private IReadOnlyList<ConfigurationChange> Set(ConfigurationDocument next)
    {
        lock (_gate)
        {
            var changes = _tenantAsCode.CompareConfigurations(_document, next);
            _document = next;
            Directory.CreateDirectory(_environment.DataDirectory);
            var temporary = _environment.ConfigurationFile + ".tmp";
            File.WriteAllText(temporary, _tenantAsCode.SerializeConfiguration(next));
            File.Move(temporary, _environment.ConfigurationFile, overwrite: true);
            return changes;
        }
    }

    private ConfigurationDocument? LoadSaved()
    {
        try
        {
            return File.Exists(_environment.ConfigurationFile)
                ? _tenantAsCode.DeserializeConfiguration(File.ReadAllText(_environment.ConfigurationFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // A damaged saved file must not stop the server; it starts from an empty configuration.
            return null;
        }
    }

    private static ConfigurationDocument CreateDefault()
        => ConfigurationDocumentVariables.CreateDefaultDocument(DateOnly.FromDateTime(DateTime.Today));
}
