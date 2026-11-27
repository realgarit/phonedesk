namespace PhoneDesk.Mcp;

internal static class McpFiles
{
    /// <summary>
    /// Reads a JSON file an AI client named. Only .json files up to a size limit are read, so the
    /// tool cannot be used to read other files on the computer.
    /// </summary>
    public static string ReadJsonFile(string path, long maxBytes) => ReadFile(path, ".json", maxBytes);

    /// <summary>Reads a calendar file an AI client named. Only .ics files up to a size limit are read.</summary>
    public static string ReadCalendarFile(string path, long maxBytes) => ReadFile(path, ".ics", maxBytes);

    private static string ReadFile(string path, string extension, long maxBytes)
    {
        var fullPath = Path.GetFullPath(path ?? string.Empty);
        if (!fullPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only {extension} files can be read.");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"'{fullPath}' does not exist.");
        }

        if (file.Length > maxBytes)
        {
            throw new InvalidOperationException($"'{fullPath}' is larger than {maxBytes / 1024} KB and is not read.");
        }

        return File.ReadAllText(fullPath);
    }
}
