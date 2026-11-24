using PhoneDesk.Services;

namespace PhoneDesk.Operations;

public sealed record M365GroupRow(string DisplayName, string Id, string MailNickname, string Description);

public sealed record ResourceAccountRow(string DisplayName, string UserPrincipalName, string Identity, string UsageLocation);

public sealed record CallQueueRow(string Name, string Identity, string RoutingMethod, int AgentAlertTime);

public sealed record AutoAttendantRow(string Name, string Identity, string LanguageId, string TimeZoneId);

public sealed record ConnectedTenant(string Id, string Name);

/// <summary>
/// Reads the marker lines that the list, lookup and connection scripts print. The markers are part
/// of the frozen script output, so every head parses them here in one place.
/// </summary>
public static class TenantOutputParser
{
    private static readonly string[] RequiredGraphModules =
    {
        ConstantsService.PowerShellModules.MicrosoftGraphAuthentication,
        ConstantsService.PowerShellModules.MicrosoftGraphUsers,
        ConstantsService.PowerShellModules.MicrosoftGraphUsersActions,
        ConstantsService.PowerShellModules.MicrosoftGraphGroups,
        ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement
    };

    public static IReadOnlyList<M365GroupRow> ParseGroups(string? output)
        => ParseRows(output, "GROUP:")
            .Select(parts => new M365GroupRow(parts[0], parts[1], parts[2], parts[3]))
            .ToList();

    public static IReadOnlyList<ResourceAccountRow> ParseResourceAccounts(string? output)
        => ParseRows(output, "RESOURCEACCOUNT:")
            .Select(parts => new ResourceAccountRow(parts[0], parts[1], parts[2], parts[3]))
            .ToList();

    public static IReadOnlyList<CallQueueRow> ParseCallQueues(string? output)
        => ParseRows(output, "CALLQUEUE:")
            .Select(parts => new CallQueueRow(
                parts[0], parts[1], parts[2], int.TryParse(parts[3], out var alertTime) ? alertTime : 0))
            .ToList();

    public static IReadOnlyList<AutoAttendantRow> ParseAutoAttendants(string? output)
        => ParseRows(output, "AUTOATTENDANT:")
            .Select(parts => new AutoAttendantRow(parts[0], parts[1], parts[2], parts[3]))
            .ToList();

    /// <summary>Returns the group ID from a <c>M365GROUPID:</c> line, or null when there is none.</summary>
    public static string? ParseM365GroupId(string? output)
    {
        const string marker = "M365GROUPID:";
        foreach (var line in Lines(output))
        {
            if (line.StartsWith(marker, StringComparison.Ordinal) && line.Length > marker.Length)
            {
                return line[marker.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>Returns the audio file ID from an <c>AUDIOFILEID:</c> line, or null when there is none.</summary>
    public static string? ParseAudioFileId(string? output)
    {
        const string marker = "AUDIOFILEID:";
        foreach (var line in Lines(output))
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var id = line[(index + marker.Length)..].Trim();
            if (Guid.TryParse(id, out _))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Returns the tenant from the <c>Connected to tenant: Name (Id)</c> line of the Teams connect script.</summary>
    public static ConnectedTenant? ParseConnectedTenant(string? output)
    {
        ConnectedTenant? tenant = null;
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            if (!line.Contains("Connected to tenant:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(':')[1].Trim().Split('(');
            if (parts.Length >= 2)
            {
                tenant = new ConnectedTenant(parts[1].Trim(')').Trim(), parts[0].Trim());
            }
        }

        return tenant;
    }

    /// <summary>Returns the required modules that the module check did not report as available.</summary>
    public static IReadOnlyList<string> FindMissingModules(string? output)
    {
        output ??= string.Empty;
        var missing = new List<string>();
        if (!output.Contains("MicrosoftTeams module is available", StringComparison.Ordinal) &&
            !output.Contains("MicrosoftTeams module installed successfully", StringComparison.Ordinal))
        {
            missing.Add(ConstantsService.PowerShellModules.MicrosoftTeams);
        }

        missing.AddRange(RequiredGraphModules.Where(
            module => !output.Contains(module + " module is available", StringComparison.Ordinal)));
        return missing;
    }

    private static IEnumerable<string[]> ParseRows(string? output, string marker)
    {
        foreach (var line in Lines(output))
        {
            if (!line.StartsWith(marker, StringComparison.Ordinal) || line.Length <= marker.Length)
            {
                continue;
            }

            var parts = line[marker.Length..].Split('|');
            if (parts.Length >= 4)
            {
                yield return parts.Select(part => part.Trim()).ToArray();
            }
        }
    }

    private static string[] Lines(string? output)
        => (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
