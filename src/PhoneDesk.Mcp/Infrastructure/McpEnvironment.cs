namespace PhoneDesk.Mcp;

/// <summary>Where the server keeps its files and which safety switches are set.</summary>
internal sealed class McpEnvironment
{
    public const string DataDirectoryVariable = "PHONEDESK_MCP_DATA_DIR";
    public const string SkipBackupVariable = "PHONEDESK_MCP_SKIP_BACKUP";
    public const string AllowDeleteExistingVariable = "PHONEDESK_MCP_ALLOW_DELETE_EXISTING";

    public McpEnvironment()
        : this(
            Environment.GetEnvironmentVariable(DataDirectoryVariable),
            IsSet(SkipBackupVariable),
            IsSet(AllowDeleteExistingVariable))
    {
    }

    public McpEnvironment(string? dataDirectory, bool skipBackup, bool allowDeleteExisting)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneDesk", "mcp")
            : dataDirectory;
        SkipBackup = skipBackup;
        AllowDeleteExisting = allowDeleteExisting;
    }

    public string DataDirectory { get; }

    /// <summary>When true, a change runs without the automatic tenant backup.</summary>
    public bool SkipBackup { get; }

    /// <summary>When true, delete tools also accept objects this server did not create.</summary>
    public bool AllowDeleteExisting { get; }

    public string ConfigurationFile => Path.Combine(DataDirectory, "configuration.json");

    public string CreatedObjectsFile => Path.Combine(DataDirectory, "created-objects.json");

    public string ExportDirectory => Path.Combine(DataDirectory, "exports");

    public string BackupDirectory(string tenantId) => Path.Combine(DataDirectory, "backups", SafeName(tenantId));

    private static string SafeName(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '_'));

    private static bool IsSet(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
