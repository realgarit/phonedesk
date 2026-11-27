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

    /// <summary>
    /// True for a path on another computer or a device: a share (\\server\share), a device path
    /// (\\.\ or \\?\) or a file URL. Opening one makes this computer sign in to that server.
    /// </summary>
    internal static bool IsNetworkOrDevicePath(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        return text.StartsWith(@"\\", StringComparison.Ordinal) ||
               text.StartsWith("//", StringComparison.Ordinal) ||
               text.Contains("://", StringComparison.Ordinal);
    }

    private static string ReadFile(string path, string extension, long maxBytes)
    {
        if (IsNetworkOrDevicePath(path))
        {
            throw new ArgumentException("Only a file on a drive of this computer can be read, not a network or device path.");
        }

        var fullPath = Path.GetFullPath(path ?? string.Empty);
        if (IsNetworkOrDevicePath(fullPath))
        {
            throw new ArgumentException("Only a file on a drive of this computer can be read, not a network or device path.");
        }

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
