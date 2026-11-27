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

    /// <summary>
    /// The full path of a file an AI client named, when it is on a drive of this computer. The
    /// path is checked as it was given and again after it was made absolute, and a drive letter
    /// that is mapped to a share is refused too.
    /// </summary>
    public static string LocalFullPath(string? path)
    {
        const string refusal = "Only a file on a drive of this computer can be used, not a network or device path.";
        if (IsNetworkOrDevicePath(path))
        {
            throw new ArgumentException(refusal);
        }

        var fullPath = Path.GetFullPath(path ?? string.Empty);
        if (IsNetworkOrDevicePath(fullPath) || !Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException(refusal);
        }

        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            var isDriveLetter = root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1] == ':';
            if (!isDriveLetter || new DriveInfo(root).DriveType == DriveType.Network)
            {
                throw new ArgumentException(refusal);
            }
        }

        return fullPath;
    }

    private static string ReadFile(string path, string extension, long maxBytes)
    {
        var fullPath = LocalFullPath(path);
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
